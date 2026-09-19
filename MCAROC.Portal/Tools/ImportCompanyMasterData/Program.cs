// Dev-only utility: bulk-loads the MCA "master data" name→identifier lists (company/CIN, LLP/LLPIN,
// foreign-company/FCRN) into the CompanyMasterRecords table, so AutoFetchController.Search can resolve
// a company name the caller knows to the CIN/LLPIN it needs without depending on the external reference
// tool's live search. Never commits/reads the source CSVs into the repo — paths are supplied as
// arguments, pointing at wherever the downloaded master-data extract lives on disk locally.
//
// The three MCA master lists use different columns and identifier formats (CIN/LLPIN/FCRN never
// collide), so they're unified into one table via RecordType rather than three separate tables — the
// only thing every caller of this table wants is "given a name, hand back an identifier".
//
// This is a full-replace load, not an incremental upsert: MCA republishes the whole extract each time,
// and at ~3.7M rows a row-by-row diff/merge isn't worth it. The replace itself loads into a separate
// staging table and only swaps it in via a single renaming transaction once every row has loaded
// successfully — a parse error, dropped connection, or bulk-copy failure partway through leaves the live
// table exactly as it was (never truncated-and-then-stuck-empty, never left half-loaded).
//
// Usage:
//   dotnet run --project Tools/ImportCompanyMasterData -- ^
//     "<folder of company_master_data_*.csv parts>" "<llp_master_data.csv>" "<foreign_master_data.csv>" [--connection="..."]
//
// With no arguments, defaults to the paths the 2026-09-19 MCA extract was downloaded to.

using System.Data;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;

const int BatchSize = 100_000;

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var companyFolder = positional.ElementAtOrDefault(0) ?? @"E:\Downloads\company_master_data_2026-09-19";
var llpFile = positional.ElementAtOrDefault(1) ?? @"E:\Downloads\llp_master_data_2026-09-19.csv";
var foreignFile = positional.ElementAtOrDefault(2) ?? @"E:\Downloads\foreign_master_data_2026-09-19.csv";

// Not a secret — Windows Integrated Auth against the local dev instance, same default as
// appsettings.Development.json. Override with --connection="..." for any other target (e.g. a shared
// or production database).
var connectionString = args.FirstOrDefault(a => a.StartsWith("--connection=", StringComparison.Ordinal))?["--connection=".Length..]
    ?? @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis;Trusted_Connection=True;TrustServerCertificate=True;";

if (!Directory.Exists(companyFolder))
{
    Console.Error.WriteLine($"Company master folder not found: {companyFolder}");
    return 1;
}
if (!File.Exists(llpFile))
{
    Console.Error.WriteLine($"LLP master file not found: {llpFile}");
    return 1;
}
if (!File.Exists(foreignFile))
{
    Console.Error.WriteLine($"Foreign-company master file not found: {foreignFile}");
    return 1;
}

var companyFiles = Directory.GetFiles(companyFolder, "*.csv").OrderBy(p => p, StringComparer.Ordinal).ToArray();
if (companyFiles.Length == 0)
{
    Console.Error.WriteLine($"No .csv files found under: {companyFolder}");
    return 1;
}

Console.WriteLine($"Company master parts: {companyFiles.Length} file(s) in {companyFolder}");
Console.WriteLine($"LLP master: {llpFile}");
Console.WriteLine($"Foreign master: {foreignFile}");
Console.WriteLine($"Target: {RedactedConnectionSummary(connectionString)}");
Console.WriteLine();

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

// Load into a staging copy of the table, not the live one — the live table is never truncated, so a
// crash or bad batch partway through the ~3.7M-row load leaves it exactly as it was, still fully
// queryable by AutoFetchController.Search the whole time. Only a successful full load ever reaches
// SwapStagingIntoLiveAsync.
await using (var dropOldStaging = new SqlCommand(
    "IF OBJECT_ID('dbo.CompanyMasterRecords_Old') IS NOT NULL DROP TABLE dbo.CompanyMasterRecords_Old; " +
    "IF OBJECT_ID('dbo.CompanyMasterRecords_Staging') IS NOT NULL DROP TABLE dbo.CompanyMasterRecords_Staging;",
    connection))
    await dropOldStaging.ExecuteNonQueryAsync(); // clears any leftovers from a previous failed run too

await using (var createStaging = new SqlCommand(
    "SELECT TOP (0) * INTO dbo.CompanyMasterRecords_Staging FROM dbo.CompanyMasterRecords; " +
    "ALTER TABLE dbo.CompanyMasterRecords_Staging ADD CONSTRAINT PK_CompanyMasterRecords_Staging PRIMARY KEY (Identifier); " +
    "CREATE INDEX IX_CompanyMasterRecords_Staging_RecordType_Name ON dbo.CompanyMasterRecords_Staging (RecordType, Name);",
    connection))
    await createStaging.ExecuteNonQueryAsync();
Console.WriteLine("Created staging table — loading into it (live CompanyMasterRecords is untouched until the load fully succeeds).");

// The MCA extract itself repeats some identifiers (seen: the same CIN twice within one company-master
// part, ~2.16M rows in) — not a bug in this tool, just real dirty source data. Tracked across the whole
// run, not just per file/batch, since a dup could straddle a batch or file boundary; ~3.7M short strings
// is a trivial amount of memory to hold for a one-shot import.
var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);

var total = 0L;
foreach (var file in companyFiles)
    total += await LoadAsync(file, CompanyMasterRecordType.Company, ReadCompanyRow, connection, seenIdentifiers);
total += await LoadAsync(llpFile, CompanyMasterRecordType.Llp, ReadLlpRow, connection, seenIdentifiers);
total += await LoadAsync(foreignFile, CompanyMasterRecordType.Foreign, ReadForeignRow, connection, seenIdentifiers);

Console.WriteLine();
Console.WriteLine("Every row loaded into staging — swapping it in as the live table now.");
// sp_rename on a table renames only the table object — the PK constraint it carries keeps its old name
// (PK_CompanyMasterRecords) regardless. The old table's constraint must be renamed out of the way
// BEFORE the staging constraint claims that name, or the second rename collides with it (constraint
// names are schema-scoped-unique, unlike index names).
//
// sp_rename reports failure via RAISERROR + a nonzero return code, NOT as a batch-aborting runtime
// error — proven empirically (a real run of this tool once left the database half-swapped): neither an
// implicit rollback-on-disconnect NOR `SET XACT_ABORT ON` catches a failing sp_rename, because the
// engine never treats the call as having thrown — the batch just continues to the next statement and
// COMMIT still runs, silently keeping every rename that happened to succeed. The only reliable signal is
// sp_rename's own return code, checked and acted on explicitly after every single call.
await using (var swap = new SqlCommand(
    """
    BEGIN TRANSACTION;
    DECLARE @rc int;

    EXEC @rc = sp_rename 'dbo.CompanyMasterRecords', 'CompanyMasterRecords_Old';
    IF @rc <> 0 BEGIN ROLLBACK TRANSACTION; THROW 50000, 'Swap step 1/5 failed: rename live table to _Old.', 1; END

    EXEC @rc = sp_rename 'dbo.PK_CompanyMasterRecords', 'PK_CompanyMasterRecords_Old', 'OBJECT';
    IF @rc <> 0 BEGIN ROLLBACK TRANSACTION; THROW 50000, 'Swap step 2/5 failed: rename old table''s PK out of the way.', 1; END

    EXEC @rc = sp_rename 'dbo.CompanyMasterRecords_Staging', 'CompanyMasterRecords';
    IF @rc <> 0 BEGIN ROLLBACK TRANSACTION; THROW 50000, 'Swap step 3/5 failed: rename staging table live.', 1; END

    EXEC @rc = sp_rename 'dbo.PK_CompanyMasterRecords_Staging', 'PK_CompanyMasterRecords', 'OBJECT';
    IF @rc <> 0 BEGIN ROLLBACK TRANSACTION; THROW 50000, 'Swap step 4/5 failed: rename new table''s PK to canonical name.', 1; END

    EXEC @rc = sp_rename 'dbo.CompanyMasterRecords.IX_CompanyMasterRecords_Staging_RecordType_Name', 'IX_CompanyMasterRecords_RecordType_Name', 'INDEX';
    IF @rc <> 0 BEGIN ROLLBACK TRANSACTION; THROW 50000, 'Swap step 5/5 failed: rename new table''s index to canonical name.', 1; END

    COMMIT;
    """,
    connection)
{ CommandTimeout = 120 })
    await swap.ExecuteNonQueryAsync();

try
{
    await using var dropOld = new SqlCommand("DROP TABLE dbo.CompanyMasterRecords_Old", connection) { CommandTimeout = 120 };
    await dropOld.ExecuteNonQueryAsync();
}
catch (SqlException ex)
{
    // The swap already committed — the new data is live either way. Leaving the old copy around costs
    // disk space, not correctness, so this is a warning, not a failed run.
    Console.WriteLine($"Warning: swap succeeded but couldn't drop the old table copy ({ex.Message}). Drop dbo.CompanyMasterRecords_Old manually when convenient.");
}

Console.WriteLine($"Done. {total:N0} rows loaded into CompanyMasterRecords.");
return 0;

static string RedactedConnectionSummary(string connectionString)
{
    try
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var auth = builder.IntegratedSecurity ? "Windows integrated auth" : "SQL login (password redacted)";
        return $"Server={builder.DataSource}; Database={builder.InitialCatalog}; {auth}";
    }
    catch (Exception)
    {
        return "(connection string could not be parsed — not logging it, it may contain a credential)";
    }
}

static async Task<long> LoadAsync(string path, CompanyMasterRecordType recordType,
    Func<CsvReader, CompanyMasterRecordType, CompanyMasterRecord?> readRow, SqlConnection connection,
    HashSet<string> seenIdentifiers)
{
    Console.WriteLine($"Loading {recordType} rows from {Path.GetFileName(path)}...");
    var table = NewTable();
    long rowsForFile = 0, batches = 0, duplicates = 0;

    var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        MissingFieldFound = null,
        BadDataFound = null,
    };
    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, csvConfig);
    await csv.ReadAsync();
    csv.ReadHeader();

    while (await csv.ReadAsync())
    {
        var record = readRow(csv, recordType);
        if (record is null) continue; // missing identifier or name — not a usable lookup row
        if (!seenIdentifiers.Add(record.Identifier)) { duplicates++; continue; } // keep the first occurrence

        AddRow(table, record);
        rowsForFile++;

        if (table.Rows.Count >= BatchSize)
        {
            await BulkInsertAsync(connection, table);
            batches++;
            Console.WriteLine($"  ...{rowsForFile:N0} rows ({batches} batches)");
            table.Clear();
        }
    }

    if (table.Rows.Count > 0)
        await BulkInsertAsync(connection, table);

    var dupNote = duplicates > 0 ? $" ({duplicates:N0} duplicate identifiers skipped)" : "";
    Console.WriteLine($"  {recordType}: {rowsForFile:N0} rows total from {Path.GetFileName(path)}.{dupNote}");
    return rowsForFile;
}

static CompanyMasterRecord? ReadCompanyRow(CsvReader csv, CompanyMasterRecordType recordType)
{
    var identifier = Field(csv, "CIN")?.ToUpperInvariant();
    var name = Field(csv, "Company Name");
    if (identifier is null || name is null) return null;

    return new CompanyMasterRecord
    {
        Identifier = identifier,
        RecordType = recordType,
        Name = name,
        RegistrationDate = ParseDate(Field(csv, "Company Registration Date")),
        Category = Field(csv, "Company Category"),
        Class = Field(csv, "Company Class"),
        ListingStatus = Field(csv, "Listing Status"),
        AuthorizedCapital = ParseDecimal(Field(csv, "Authorized Capital")),
        PaidupCapital = ParseDecimal(Field(csv, "Paidup Capital")),
        Roc = Field(csv, "Company ROC"),
        Address = Field(csv, "Company Address"),
        PinCode = Field(csv, "Pin Code"),
        State = Field(csv, "Company State"),
        Status = Field(csv, "Company Status"),
        SubCategory = Field(csv, "Company Sub Category"),
        IndustrialClassification = Field(csv, "Company Industrial Classification"),
    };
}

static CompanyMasterRecord? ReadLlpRow(CsvReader csv, CompanyMasterRecordType recordType)
{
    var identifier = Field(csv, "LLPin")?.ToUpperInvariant();
    var name = Field(csv, "LLP  Name") ?? Field(csv, "LLP Name");
    if (identifier is null || name is null) return null;

    return new CompanyMasterRecord
    {
        Identifier = identifier,
        RecordType = recordType,
        Name = name,
        RegistrationDate = ParseDate(Field(csv, "Company Registration Date")),
        Roc = Field(csv, "Company ROC"),
        Address = Field(csv, "Company Address"),
        State = Field(csv, "Company State"),
        District = Field(csv, "Company District"),
        Status = Field(csv, "Company Status"),
        IndustrialClassification = Field(csv, "Company Industrial Classification"),
    };
}

static CompanyMasterRecord? ReadForeignRow(CsvReader csv, CompanyMasterRecordType recordType)
{
    var identifier = Field(csv, "FCRN")?.ToUpperInvariant();
    var name = Field(csv, "Company  Name") ?? Field(csv, "Company Name");
    if (identifier is null || name is null) return null;

    return new CompanyMasterRecord
    {
        Identifier = identifier,
        RecordType = recordType,
        Name = name,
        Roc = Field(csv, "Company ROC"),
        Address = Field(csv, "Company Address"),
        Country = Field(csv, "Company Country"),
        Status = Field(csv, "Company Status"),
        IndustrialClassification = Field(csv, "Company Industrial Classification"),
    };
}

static string? Field(CsvReader csv, string header)
{
    if (!csv.HeaderRecord!.Contains(header)) return null;
    var value = csv.GetField(header);
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static DateOnly? ParseDate(string? raw) =>
    raw is not null && DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

static decimal? ParseDecimal(string? raw) =>
    raw is not null && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

static DataTable NewTable()
{
    var table = new DataTable();
    table.Columns.Add("Identifier", typeof(string));
    table.Columns.Add("RecordType", typeof(string));
    table.Columns.Add("Name", typeof(string));
    table.Columns.Add("RegistrationDate", typeof(DateTime));
    table.Columns.Add("Category", typeof(string));
    table.Columns.Add("Class", typeof(string));
    table.Columns.Add("ListingStatus", typeof(string));
    table.Columns.Add("AuthorizedCapital", typeof(decimal));
    table.Columns.Add("PaidupCapital", typeof(decimal));
    table.Columns.Add("Roc", typeof(string));
    table.Columns.Add("Address", typeof(string));
    table.Columns.Add("PinCode", typeof(string));
    table.Columns.Add("State", typeof(string));
    table.Columns.Add("District", typeof(string));
    table.Columns.Add("Country", typeof(string));
    table.Columns.Add("Status", typeof(string));
    table.Columns.Add("SubCategory", typeof(string));
    table.Columns.Add("IndustrialClassification", typeof(string));
    return table;
}

static void AddRow(DataTable table, CompanyMasterRecord record)
{
    var row = table.NewRow();
    row["Identifier"] = record.Identifier;
    row["RecordType"] = record.RecordType.ToString();
    row["Name"] = record.Name;
    row["RegistrationDate"] = (object?)record.RegistrationDate?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value;
    row["Category"] = (object?)record.Category ?? DBNull.Value;
    row["Class"] = (object?)record.Class ?? DBNull.Value;
    row["ListingStatus"] = (object?)record.ListingStatus ?? DBNull.Value;
    row["AuthorizedCapital"] = (object?)record.AuthorizedCapital ?? DBNull.Value;
    row["PaidupCapital"] = (object?)record.PaidupCapital ?? DBNull.Value;
    row["Roc"] = (object?)record.Roc ?? DBNull.Value;
    row["Address"] = (object?)record.Address ?? DBNull.Value;
    row["PinCode"] = (object?)record.PinCode ?? DBNull.Value;
    row["State"] = (object?)record.State ?? DBNull.Value;
    row["District"] = (object?)record.District ?? DBNull.Value;
    row["Country"] = (object?)record.Country ?? DBNull.Value;
    row["Status"] = (object?)record.Status ?? DBNull.Value;
    row["SubCategory"] = (object?)record.SubCategory ?? DBNull.Value;
    row["IndustrialClassification"] = (object?)record.IndustrialClassification ?? DBNull.Value;
    table.Rows.Add(row);
}

static async Task BulkInsertAsync(SqlConnection connection, DataTable table)
{
    // TableLock: without it, every batch is fully logged even under SIMPLE recovery, and the resulting
    // log growth (in fixed 64 MB steps, always zero-filled synchronously regardless of instant file
    // initialization) is what stalled the very first run of this tool past its bulk-copy timeout.
    // BulkCopyTimeout 0 (unlimited) is the safety net on top of pre-growing the DB files in advance.
    using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, externalTransaction: null)
    {
        DestinationTableName = "dbo.CompanyMasterRecords_Staging",
        BulkCopyTimeout = 0,
        BatchSize = BatchSize,
    };
    foreach (DataColumn column in table.Columns)
        bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
    await bulk.WriteToServerAsync(table);
}
