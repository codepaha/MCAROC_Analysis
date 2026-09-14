using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class CoastalFinancialLinkTests
{
    private const string ZipPath = @"E:\Downloads\Coastal data\COASTAL PROJECTS LIMITED Documents.zip";
    private const string RocPath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982.xls";

    private const string ExpectedBaselineSha256 = "543b23165b6fee4511b3dd9d109b35ca7b05bbc210a4a74416229383ff97774d";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    static CoastalFinancialLinkTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static string GetFixturePath()
    {
        var localBin = Path.Combine(AppContext.BaseDirectory, "Fixtures", "coastal_d3_baseline.json");
        if (File.Exists(localBin)) return localBin;

        var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "coastal_d3_baseline.json"));
        if (File.Exists(sourceDir)) return sourceDir;

        return localBin;
    }

    private static List<CoastalFinancialLinkResultEntry> LoadBaselineEntries()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture not found at {fixturePath}");

        var json = File.ReadAllText(fixturePath, Encoding.UTF8);
        var entries = JsonSerializer.Deserialize<List<CoastalFinancialLinkResultEntry>>(json, JsonOptions);
        Assert.NotNull(entries);
        return entries;
    }

    private static (List<FinancialYearData> Years, List<FinancialFact> Facts, FinancialLinkTargetCatalog Catalog) LoadCoastalFinancialData()
    {
        var reader = new ExcelSheetReader();
        var rocWorkbook = reader.ReadWorkbook(RocPath);

        var standaloneSheet = rocWorkbook.FirstOrDefault(s => s.Name.Equals("Standalone Financial Data", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Standalone Financial Data sheet not found");
        var consolidatedSheet = rocWorkbook.FirstOrDefault(s => s.Name.Equals("Consolidated Financial Data", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Consolidated Financial Data sheet not found");

        var standaloneResult = StandaloneFinancialDataParser.Parse(
            standaloneSheet, 0, 0, null, out var standaloneFacts, out var standaloneCatalog, FinancialBasis.Standalone);

        var consolidatedResult = StandaloneFinancialDataParser.Parse(
            consolidatedSheet, 0, 0, null, out var consolidatedFacts, out var consolidatedCatalog, FinancialBasis.Consolidated);

        var allYears = new List<FinancialYearData>();
        allYears.AddRange(standaloneResult.Items);
        allYears.AddRange(consolidatedResult.Items);

        var allFacts = new List<FinancialFact>();
        allFacts.AddRange(standaloneFacts);
        allFacts.AddRange(consolidatedFacts);

        var combinedCatalog = new FinancialLinkTargetCatalog();
        combinedCatalog.AddRange(standaloneCatalog.AllTargets);
        combinedCatalog.AddRange(consolidatedCatalog.AllTargets);

        return (allYears, allFacts, combinedCatalog);
    }

    [Fact]
    public void VerifyBaselineFixture_IntegrityAndHash()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture not found at {fixturePath}");

        var fileBytes = File.ReadAllBytes(fixturePath);
        var hash = SHA256.HashData(fileBytes);
        var hashHex = Convert.ToHexString(hash).ToLowerInvariant();

        Assert.Equal(ExpectedBaselineSha256, hashHex);

        var entries = LoadBaselineEntries();
        Assert.Equal(814, entries.Count);

        // Verify strict ordinal sort order: OuterEntryFullPath, then NestedEntryRelativePath
        for (var i = 0; i < entries.Count - 1; i++)
        {
            var current = entries[i];
            var next = entries[i + 1];

            var outerCmp = string.Compare(current.OuterEntryFullPath, next.OuterEntryFullPath, StringComparison.Ordinal);
            Assert.True(outerCmp <= 0, $"Entries out of order at index {i} on OuterEntryFullPath: '{current.OuterEntryFullPath}' > '{next.OuterEntryFullPath}'");

            if (outerCmp == 0)
            {
                var nestedCmp = string.Compare(current.NestedEntryRelativePath, next.NestedEntryRelativePath, StringComparison.Ordinal);
                Assert.True(nestedCmp < 0, $"Duplicate or out-of-order nested path at index {i}: '{current.NestedEntryRelativePath}' >= '{next.NestedEntryRelativePath}'");
            }
        }
    }

    [Fact]
    public void VerifyBaselineFixture_PartitionMath()
    {
        var entries = LoadBaselineEntries();

        var duplicates = entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.ManifestDuplicateBypassed);
        var outOfScope = entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.UnlinkedOutOfScope);
        var autoAccepted = entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.AutoAccepted);
        var pendingReview = entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.PendingReview);
        var unlinkedNoCand = entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.UnlinkedNoCandidate);

        var periodNotFound = entries.Count(e => e.Reason == PilotFinancialLinkReason.PeriodNotFoundInWorkbook);
        var missingPeriod = entries.Count(e => e.Reason == PilotFinancialLinkReason.MissingReportingPeriod);
        var conflictingBasis = entries.Count(e => e.Reason == PilotFinancialLinkReason.ConflictingBasisEvidence);
        var valueMismatch = entries.Count(e => e.Reason == PilotFinancialLinkReason.StatementValueMismatch);
        var exactPeriod = entries.Count(e => e.Reason == PilotFinancialLinkReason.ExactMatchReportingPeriod);

        Assert.Equal(169, duplicates);
        Assert.Equal(615, outOfScope);
        Assert.Equal(8, autoAccepted);
        Assert.Equal(5, pendingReview);
        Assert.Equal(17, unlinkedNoCand);

        Assert.Equal(12, periodNotFound);
        Assert.Equal(5, missingPeriod);
        Assert.Equal(1, conflictingBasis);
        Assert.Equal(1, valueMismatch);
        Assert.Equal(3, exactPeriod);

        // Exclusive partition sums
        Assert.Equal(814, duplicates + outOfScope + autoAccepted + pendingReview + unlinkedNoCand);
        Assert.Equal(17, periodNotFound + missingPeriod);
        Assert.Equal(5, conflictingBasis + valueMismatch + exactPeriod);
    }

    [Fact]
    public void VerifyBaselineFixture_30IndependentOracleEntries()
    {
        var entries = LoadBaselineEntries();
        var candidates = entries.Where(e => e.Outcome != PilotFinancialLinkOutcome.ManifestDuplicateBypassed &&
                                            e.Outcome != PilotFinancialLinkOutcome.UnlinkedOutOfScope).ToList();

        Assert.Equal(30, candidates.Count);

        var oracleEntries = new (string OuterPath, string NestedPath, string Sha256, PilotFinancialLinkOutcome Outcome, PilotFinancialLinkReason Reason, int? FinancialYear, FinancialBasis? Basis)[]
        {
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/25a87bec0f34edf2d9e851021ac01042v1-Form AOC-4(XBRL)-09032017_signed.pdf",
             "ed104bf5c9ccf3e8b8c4aece82ff6397304050f01fcbcec504cc29fea646b6b9",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.MissingReportingPeriod,
             null,
             null),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/2d423197c44eaf5f75e65e09f4be987fv1-XBRL document in respect Consolidated financial statement.pdf",
             "3958cd671cf9a6d2d8ff20d7da7beb187410ba9aa6f3b6aa8f4a782aa9915940",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2017,
             FinancialBasis.Consolidated),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/4e66bec9feeacb276b6a415cf951b97av1-XBRL document in respect Consolidated financial statement.pdf",
             "78dd11fe3474f22ce753bc122bf6b9577a1b92b2a011c6a63256c772eb89e1b6",
             PilotFinancialLinkOutcome.PendingReview,
             PilotFinancialLinkReason.ConflictingBasisEvidence,
             2015,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/549ed5ca157ad5145de1e0444fc2ffa9v1-XBRL document in respect of balance sheet 15-01-2013 for the financial year ending on 31-03-2012.pdf.pdf",
             "6838c1eb486903884060f510ae06d4446c75023f717499a733fc22a61d493fba",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2012,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/592859b418d237333a40e4dc09bd5afbv1-XBRL financial statements duly authenticated as per section 134 (including Board's report,auditor's report and other documents).pdf",
             "2dba313f9566d49280ac2048e7d6b80e457ac15108b06ae7e094197ccf6a27e2",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2016,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/895cb444d18c37adfb79c951772edc3cv1-XBRL document in respect Consolidated financial statement.pdf",
             "be2de6ccadd7ad26447a55c986ea5b3d6ddefaefcbb4ba11b87d5e387e0223c2",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2016,
             FinancialBasis.Consolidated),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/9c6b9ffd6fa22d18a630d9394ce4c9d4v1-Form AOC-4(XBRL)-10072018_signed.pdf",
             "bca0fa964d92c329d1bb8ea7d6a691d7b79329f54c1ec0d01f8b5ff05c5481cb",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.MissingReportingPeriod,
             null,
             null),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/9dff643ddc8f5436d9a8e2e8f1504beev1-XBRL financial statements duly authenticated as per section 134 (including Board's report,auditor's report and other documents).pdf",
             "c91165d465ff9f4c3c17a55aaabee3a4c18c8224933abeed8acc535b41a3ca43",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2017,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/a3c711388f21fac7392919b05616bca7v1-Revised1 XBRL financial statements duly authenticated as per section 134 (including Board's report,auditor's report and other documents).pdf",
             "38b776411ce307d886450521a38a8e055bc4b1babe1beb6ac49d3dbb9e73a306",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2016,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/a7a8a835a5dabba94ab9aa176c107b37v1-XBRL document in respect of profit and loss account 15-01-2013 for the financial year ending on 31-03-2012.pdf.pdf",
             "456b90e4587f5e58fe2be5d0b76bdb437c03d074f2bc663513efaf6b05573d66",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2012,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/d13e84c78a56390ed213a1cec1544412v1-Form_AOC-4_XBRL_(kv)_190416_s_COASTALLTD_20160420152108.pdf-20042016.pdf",
             "a92379f9f130d395ee61f4713d0347a6c9c55b2af3879bbe96b8d7f4e53ec793",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.MissingReportingPeriod,
             null,
             null),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70909_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70909_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/f63c68237dbfcd112a2451865bdea0ffv1-Form_AOC-4_XBRL_24.05.16_s_COASTALLTD_20160524121500.pdf-13062016-signed.pdf",
             "782f43ff3a9f3438a9fd49290d69319f71b0c2676b43244d33c7d25629507981",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.MissingReportingPeriod,
             null,
             null),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/1bce3042eeb03369d63ea10c03349cc5v1-XBRL document in respect of profit and loss account 15-01-2013 for the financial year ending on 31-03-2012.pdf.pdf",
             "cf6a5ceae3253b893a4b39fd3fe9392947b58dd72e620e9b610f5fa371d58c04",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2012,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/242fdff3a78f12281efcb4a5099d0525v1-Form 23AC XBRL-161013-150113 for the FY ending on-310312.pdf",
             "443b013d9939a2f0561c56ce7ba71a81bec5e4154722519ac59ca9420f038451",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2012,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/514a91f72a3f58ef828ec14dcd4f7b1cv1-XBRL document in respect of profit and loss account 05-05-2015 for the financial year ending on 31-03-2014.pdf.pdf",
             "1348399f2f4cb708ac3e073f5b41a9be6ac29a70fe605a0d5f938c16eb7cf49d",
             PilotFinancialLinkOutcome.PendingReview,
             PilotFinancialLinkReason.StatementValueMismatch,
             2014,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/55654ee1041d74675f9802b42fed02b4v1-FormSchV-070212 for the FY ending on-310311.pdf",
             "4cc974ac9733a8cdb74622aae8b160768fb9f3facf5c60483270c147885d1395",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2011,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/5dcc2f41737c487fbcd456fd738ebf34v1-Form AOC-4(XBRL).pdf",
             "a44c651727fa7bb545726b33a2286537888e244de07de7acce514c437a015593",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.MissingReportingPeriod,
             null,
             null),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/7b039e6218ee542a822a5392cbfa86cbv1-Form 23AC XBRL-070515-050515 for the FY ending on-310314.pdf",
             "56971e59b1efe89d287dc634e86ac56aa79fb460196a5d3f2b5fcd54bbfa6b36",
             PilotFinancialLinkOutcome.PendingReview,
             PilotFinancialLinkReason.ExactMatchReportingPeriod,
             2014,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/82d24b8924c07f61e4d6154f38999640v1-Form 23ACA XBRL-160113-150113 for the FY ending on-310312.pdf",
             "476a325781337fc653f2c762a7115ba268b4e0148c6430ab3d3fbf40b2122adc",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2012,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/88a318933a223444f08cb3d70647cf78v1-XBRL financial statements duly authenticated as per section 134 (including Board's report,auditor's report and other documents).pdf",
             "837a6ea7e0ad14878bbd74347e9e8181263a3837804151f2d30c4ee3f92b7240",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2015,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/8d78607fb6b5e9a8030ec12e2f5f77dcv1-FormSchV-250214 for the FY ending on-310313.pdf",
             "6f48fb33c887a7b769e3939d395472de2b8bde63376a454549be023540d6ef17",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2013,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/a0ac3a558067648f43797f9ddd9bb1c6v1-Revised1 XBRL financial statements duly authenticated as per section 134 (including Board's report,auditor's report and other documents).pdf",
             "d1838037a008c264c5180208897d3798648b901cbfb3cf51426930726b10fd85",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2015,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/c536cea3f8f45c02c91869cf22c39679v1-Form 23ACA XBRL-060515-050515 for the FY ending on-310314.pdf",
             "3a7d65e5363004c382d85880122d5ab05a2ae78903dd1f2b84824785ffe58b05",
             PilotFinancialLinkOutcome.PendingReview,
             PilotFinancialLinkReason.ExactMatchReportingPeriod,
             2014,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/c75d3d0e9428e6724095bb6f4dfb849bv1-XBRL document in respect of balance sheet 03-11-2013 for the financial year ending on 31-03-2013.pdf.pdf",
             "2fdd87ab926779f577c63e2fadbf2b68a002c0db7ca2b511e6758affb6ee6e02",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2013,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/d266d4e0a1c98fdab97c0ce5deeb76dav1-FormSchV-230215 for the FY ending on-310314.pdf",
             "b1cd81e602e9f04017e040db8ad631586b73b51d9dc4be39d463921051a48ae9",
             PilotFinancialLinkOutcome.PendingReview,
             PilotFinancialLinkReason.ExactMatchReportingPeriod,
             2014,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/d2d78e9ee47934fbf4321af384985fb8v1-XBRL document in respect of profit and loss account 03-11-2013 for the financial year ending on 31-03-2013.pdf.pdf",
             "340db16681e508cb9b264be9e621fd81dc4a73d1bfba8d0619ae41536833f536",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2013,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/e434c5297f9bce28f6d342a647cd1fc7v1-FormSchV-210213 for the FY ending on-310312.pdf",
             "7f3aad19295c06176cfd5708f286915051e843cbb6e18ede0c0f2c71146d1d47",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2012,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/e7944bf06eaaf00093d4b6d0d4e35fddv1-Form 23AC XBRL-081113-031113 for the FY ending on-310313.pdf",
             "2d52d2c3b0891d34c4ededd7461e0b5877c3c22363c4da26236c47071a38f313",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2013,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/f035f285095bc58da64b6cd6b08acd03v1-XBRL document in respect of balance sheet 05-05-2015 for the financial year ending on 31-03-2014.pdf.pdf",
             "e64ed9fa11871f8cbe1be74d6039111f6b6edbe659689e9353da7a9e09f62799",
             PilotFinancialLinkOutcome.AutoAccepted,
             PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
             2014,
             FinancialBasis.Standalone),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70910_COASTAL_PROJECTS_U45203OR1995PLC003982.zip",
             "70910_COASTAL_PROJECTS_U45203OR1995PLC003982/Annual Returns and balance sheet Eform/f380463e2c7262c0758660c394beff58v1-Form 23ACA XBRL-081113-031113 for the FY ending on-310313.pdf",
             "613b9a03906329c8927c7bf0055e6d3a4fbb8ad7a1933bf40b6bcf538bbcd1ae",
             PilotFinancialLinkOutcome.UnlinkedNoCandidate,
             PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
             2013,
             FinancialBasis.Standalone),
        };

        Assert.Equal(oracleEntries.Length, candidates.Count);

        for (var i = 0; i < oracleEntries.Length; i++)
        {
            var expected = oracleEntries[i];
            var actual = candidates[i];

            Assert.Equal(expected.OuterPath, actual.OuterEntryFullPath);
            Assert.Equal(expected.NestedPath, actual.NestedEntryRelativePath);
            Assert.Equal(expected.Sha256, actual.Sha256Hex);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.Reason, actual.Reason);
            Assert.Equal(expected.FinancialYear, actual.MatchedFinancialYear);
            Assert.Equal(expected.Basis, actual.MatchedBasis);
        }
    }

    [Fact]
    public void VerifyBaselineFixture_8AutoAcceptedExhaustive()
    {
        var entries = LoadBaselineEntries();
        var entriesBySha = entries
            .Where(e => e.Outcome == PilotFinancialLinkOutcome.AutoAccepted)
            .ToDictionary(e => e.Sha256Hex, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(8, entriesBySha.Count);

        // 1. SHA: 3958cd67
        AssertAutoAccepted(
            entriesBySha["3958cd671cf9a6d2d8ff20d7da7beb187410ba9aa6f3b6aa8f4a782aa9915940"],
            expectedYear: 2017,
            expectedBasis: FinancialBasis.Consolidated,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 7,
            expectedLineItem: "ShareCapital",
            expectedValue: 330.94m,
            expectedPage: 17,
            expectedSheet: "Consolidated Financial Data",
            expectedRow: 4,
            expectedCol: 5,
            expectedHeader: "31 Mar, 2017",
            expectedRowLabel: "Share Capital");

        // 2. SHA: 2dba313f
        AssertAutoAccepted(
            entriesBySha["2dba313f9566d49280ac2048e7d6b80e457ac15108b06ae7e094197ccf6a27e2"],
            expectedYear: 2016,
            expectedBasis: FinancialBasis.Standalone,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 3,
            expectedLineItem: "ShareCapital",
            expectedValue: 330.94m,
            expectedPage: 33,
            expectedSheet: "Standalone Financial Data",
            expectedRow: 4,
            expectedCol: 5,
            expectedHeader: "31 Mar, 2016",
            expectedRowLabel: "Share Capital");

        // 3. SHA: be2de6cc
        AssertAutoAccepted(
            entriesBySha["be2de6ccadd7ad26447a55c986ea5b3d6ddefaefcbb4ba11b87d5e387e0223c2"],
            expectedYear: 2016,
            expectedBasis: FinancialBasis.Consolidated,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 6,
            expectedLineItem: "ShareCapital",
            expectedValue: 330.94m,
            expectedPage: 13,
            expectedSheet: "Consolidated Financial Data",
            expectedRow: 4,
            expectedCol: 4,
            expectedHeader: "31 Mar, 2016",
            expectedRowLabel: "Share Capital");

        // 4. SHA: c91165d4
        AssertAutoAccepted(
            entriesBySha["c91165d465ff9f4c3c17a55aaabee3a4c18c8224933abeed8acc535b41a3ca43"],
            expectedYear: 2017,
            expectedBasis: FinancialBasis.Standalone,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 4,
            expectedLineItem: "ShareCapital",
            expectedValue: 330.94m,
            expectedPage: 67,
            expectedSheet: "Standalone Financial Data",
            expectedRow: 4,
            expectedCol: 6,
            expectedHeader: "31 Mar, 2017",
            expectedRowLabel: "Share Capital");

        // 5. SHA: 38b77641
        AssertAutoAccepted(
            entriesBySha["38b776411ce307d886450521a38a8e055bc4b1babe1beb6ac49d3dbb9e73a306"],
            expectedYear: 2016,
            expectedBasis: FinancialBasis.Standalone,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 3,
            expectedLineItem: "ShareCapital",
            expectedValue: 330.94m,
            expectedPage: 33,
            expectedSheet: "Standalone Financial Data",
            expectedRow: 4,
            expectedCol: 5,
            expectedHeader: "31 Mar, 2016",
            expectedRowLabel: "Share Capital");

        // 6. SHA: 837a6ea7
        AssertAutoAccepted(
            entriesBySha["837a6ea7e0ad14878bbd74347e9e8181263a3837804151f2d30c4ee3f92b7240"],
            expectedYear: 2015,
            expectedBasis: FinancialBasis.Standalone,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 2,
            expectedLineItem: "ShareCapital",
            expectedValue: 152.25m,
            expectedPage: 30,
            expectedSheet: "Standalone Financial Data",
            expectedRow: 4,
            expectedCol: 4,
            expectedHeader: "31 Mar, 2015",
            expectedRowLabel: "Share Capital");

        // 7. SHA: d1838037
        AssertAutoAccepted(
            entriesBySha["d1838037a008c264c5180208897d3798648b901cbfb3cf51426930726b10fd85"],
            expectedYear: 2015,
            expectedBasis: FinancialBasis.Standalone,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 2,
            expectedLineItem: "ShareCapital",
            expectedValue: 152.25m,
            expectedPage: 30,
            expectedSheet: "Standalone Financial Data",
            expectedRow: 4,
            expectedCol: 4,
            expectedHeader: "31 Mar, 2015",
            expectedRowLabel: "Share Capital");

        // 8. SHA: e64ed9fa
        AssertAutoAccepted(
            entriesBySha["e64ed9fa11871f8cbe1be74d6039111f6b6edbe659689e9353da7a9e09f62799"],
            expectedYear: 2014,
            expectedBasis: FinancialBasis.Standalone,
            expectedKind: FinancialTargetKind.FinancialYearData,
            expectedEntityId: 1,
            expectedLineItem: "ShareCapital",
            expectedValue: 21.01m,
            expectedPage: 43,
            expectedSheet: "Standalone Financial Data",
            expectedRow: 4,
            expectedCol: 3,
            expectedHeader: "31 Mar, 2014",
            expectedRowLabel: "Share Capital");
    }

    private static void AssertAutoAccepted(
        CoastalFinancialLinkResultEntry entry,
        int expectedYear,
        FinancialBasis expectedBasis,
        FinancialTargetKind expectedKind,
        long expectedEntityId,
        string expectedLineItem,
        decimal expectedValue,
        int expectedPage,
        string expectedSheet,
        int expectedRow,
        int expectedCol,
        string expectedHeader,
        string expectedRowLabel)
    {
        Assert.Equal(PilotFinancialLinkOutcome.AutoAccepted, entry.Outcome);
        Assert.Equal(PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue, entry.Reason);
        Assert.Equal(expectedYear, entry.MatchedFinancialYear);
        Assert.Equal(expectedBasis, entry.MatchedBasis);
        Assert.Equal(expectedKind, entry.TargetKind);
        Assert.Equal(expectedEntityId, entry.TargetEntityId);
        Assert.Equal(expectedLineItem, entry.TargetLineItem);
        Assert.Equal(expectedValue, entry.MatchedValue);
        Assert.Equal(expectedPage, entry.EvidencePageNumber);
        Assert.False(string.IsNullOrWhiteSpace(entry.EvidenceTextQuote));

        Assert.NotNull(entry.TargetCoordinates);
        Assert.Equal(expectedSheet, entry.TargetCoordinates.SheetName);
        Assert.Equal(expectedRow, entry.TargetCoordinates.SourceRowNumber);
        Assert.Equal(expectedCol, entry.TargetCoordinates.SourceColumnNumber);
        Assert.Equal(expectedHeader, entry.TargetCoordinates.SourceColumnHeader);
        Assert.Equal(expectedRowLabel, entry.TargetCoordinates.RowLabel);
        Assert.Equal(expectedLineItem, entry.TargetCoordinates.TargetField);
        Assert.Equal(expectedYear, entry.TargetCoordinates.FinancialYear);
    }

    // --- REGRESSION TESTS FOR REVIEW FINDINGS ---

    private static FinancialLinkTarget CreateYearTarget(
        FinancialYearData fin,
        string sheetName,
        int row,
        int col,
        string header,
        string rowLabel,
        string targetField,
        decimal value,
        int year,
        FinancialBasis basis,
        bool yearInferred = false)
    {
        return new FinancialLinkTarget
        {
            TargetKind = FinancialTargetKind.FinancialYearData,
            TargetEntity = fin,
            Coordinates = new TargetSourceCoordinates(sheetName, row, col, header, rowLabel, targetField, year),
            FinancialYear = year,
            Basis = basis,
            TargetField = targetField,
            NumericValue = value,
            YearInferred = yearInferred
        };
    }

    [Fact]
    public void Match_WhenStatementValueMissing_RoutesToPendingReviewWithExactMatchReportingPeriod()
    {
        var catalog = new FinancialLinkTargetCatalog();
        var fin = new FinancialYearData { FinancialYear = 2014, Basis = FinancialBasis.Standalone, ShareCapital = 21.01m };
        catalog.Add(CreateYearTarget(fin, "Standalone Financial Data", 4, 3, "31 Mar, 2014", "Share Capital", nameof(FinancialYearData.ShareCapital), 21.01m, 2014, FinancialBasis.Standalone));

        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps([fin], []);

        var candidate = new ExtractedFinancialCandidate
        {
            IsFinancial = true,
            FinancialYear = 2014,
            Basis = FinancialBasis.Standalone,
            StatementPageNumber = 43,
            CorroboratedField = nameof(FinancialYearData.ShareCapital),
            CorroboratedAmount = null // Missing value!
        };

        var result = FinancialPeriodMatcher.Match(candidate, catalog, finMap, factMap);

        Assert.Equal(PilotFinancialLinkOutcome.PendingReview, result.Outcome);
        Assert.Equal(PilotFinancialLinkReason.ExactMatchReportingPeriod, result.Reason);
    }

    [Fact]
    public void Match_WhenStatementValueMismatches_RoutesToPendingReviewWithStatementValueMismatch()
    {
        var catalog = new FinancialLinkTargetCatalog();
        var fin = new FinancialYearData { FinancialYear = 2014, Basis = FinancialBasis.Standalone, ShareCapital = 21.01m };
        catalog.Add(CreateYearTarget(fin, "Standalone Financial Data", 4, 3, "31 Mar, 2014", "Share Capital", nameof(FinancialYearData.ShareCapital), 21.01m, 2014, FinancialBasis.Standalone));

        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps([fin], []);

        var candidate = new ExtractedFinancialCandidate
        {
            IsFinancial = true,
            FinancialYear = 2014,
            Basis = FinancialBasis.Standalone,
            StatementPageNumber = 43,
            CorroboratedField = nameof(FinancialYearData.ShareCapital),
            CorroboratedAmount = 99.99m // Mismatching value!
        };

        var result = FinancialPeriodMatcher.Match(candidate, catalog, finMap, factMap);

        Assert.Equal(PilotFinancialLinkOutcome.PendingReview, result.Outcome);
        Assert.Equal(PilotFinancialLinkReason.StatementValueMismatch, result.Reason);
        Assert.Equal(21.01m, result.MatchedValue);
    }

    [Fact]
    public void Match_WhenStatementValueMatches_AutoAccepts()
    {
        var catalog = new FinancialLinkTargetCatalog();
        var fin = new FinancialYearData { FinancialYear = 2014, Basis = FinancialBasis.Standalone, ShareCapital = 21.01m };
        catalog.Add(CreateYearTarget(fin, "Standalone Financial Data", 4, 3, "31 Mar, 2014", "Share Capital", nameof(FinancialYearData.ShareCapital), 21.01m, 2014, FinancialBasis.Standalone));

        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps([fin], []);

        var candidate = new ExtractedFinancialCandidate
        {
            IsFinancial = true,
            FinancialYear = 2014,
            Basis = FinancialBasis.Standalone,
            StatementPageNumber = 43,
            CorroboratedField = nameof(FinancialYearData.ShareCapital),
            CorroboratedAmount = 21.01m // Exact match!
        };

        var result = FinancialPeriodMatcher.Match(candidate, catalog, finMap, factMap);

        Assert.Equal(PilotFinancialLinkOutcome.AutoAccepted, result.Outcome);
        Assert.Equal(PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue, result.Reason);
        Assert.Equal(21.01m, result.MatchedValue);
        Assert.Equal(1, result.TargetEntityId);
    }

    [Fact]
    public void Match_WhenConsolidatedFilingLacksConsolidatedTargets_NeverFallsBackToStandalone()
    {
        var catalog = new FinancialLinkTargetCatalog();
        // Catalog ONLY has Standalone targets for 2014
        var fin = new FinancialYearData { FinancialYear = 2014, Basis = FinancialBasis.Standalone, ShareCapital = 21.01m };
        catalog.Add(CreateYearTarget(fin, "Standalone Financial Data", 4, 3, "31 Mar, 2014", "Share Capital", nameof(FinancialYearData.ShareCapital), 21.01m, 2014, FinancialBasis.Standalone));

        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps([fin], []);

        var candidate = new ExtractedFinancialCandidate
        {
            IsFinancial = true,
            FinancialYear = 2014,
            Basis = FinancialBasis.Consolidated // Strict Consolidated basis
        };

        var result = FinancialPeriodMatcher.Match(candidate, catalog, finMap, factMap);

        // Must NEVER fall back to Standalone or cross-basis auto-accept
        Assert.Equal(PilotFinancialLinkOutcome.UnlinkedNoCandidate, result.Outcome);
        Assert.Equal(PilotFinancialLinkReason.PeriodNotFoundInWorkbook, result.Reason);
        Assert.Equal(FinancialBasis.Consolidated, result.MatchedBasis);
    }

    [Fact]
    public void Match_WhenConflictingBasisEvidence_RoutesToPendingReviewWithConflictingBasisEvidence()
    {
        var catalog = new FinancialLinkTargetCatalog();
        var fin = new FinancialYearData { FinancialYear = 2015, Basis = FinancialBasis.Standalone, ShareCapital = 152.25m };
        catalog.Add(CreateYearTarget(fin, "Standalone Financial Data", 4, 4, "31 Mar, 2015", "Share Capital", nameof(FinancialYearData.ShareCapital), 152.25m, 2015, FinancialBasis.Standalone));

        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps([fin], []);

        var candidate = new ExtractedFinancialCandidate
        {
            IsFinancial = true,
            FinancialYear = 2015,
            Basis = FinancialBasis.Standalone,
            HasConflictingBasis = true // Filename said Consolidated, header said Standalone
        };

        var result = FinancialPeriodMatcher.Match(candidate, catalog, finMap, factMap);

        Assert.Equal(PilotFinancialLinkOutcome.PendingReview, result.Outcome);
        Assert.Equal(PilotFinancialLinkReason.ConflictingBasisEvidence, result.Reason);
    }

    [Fact]
    public void Match_WhenMissingBasisEvidence_RoutesToPendingReviewWithMissingBasisEvidence()
    {
        var catalog = new FinancialLinkTargetCatalog();
        var (finMap, factMap) = FinancialLinkTargetCatalog.BuildIdMaps([], []);

        var candidate = new ExtractedFinancialCandidate
        {
            IsFinancial = true,
            FinancialYear = 2015,
            Basis = null // Unspecified/missing basis
        };

        var result = FinancialPeriodMatcher.Match(candidate, catalog, finMap, factMap);

        Assert.Equal(PilotFinancialLinkOutcome.PendingReview, result.Outcome);
        Assert.Equal(PilotFinancialLinkReason.MissingBasisEvidence, result.Reason);
    }

    [SkippableFact]
    public void VerifyService_EntityImmutabilityAndRepeatability()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath), "Coastal files not present.");

        var (years, facts, catalog) = LoadCoastalFinancialData();

        // Verify initial state: IDs are all 0
        Assert.All(years, y => Assert.Equal(0, y.FinancialId));
        Assert.All(facts, f => Assert.Equal(0, f.FinancialFactId));

        using var zipStream1 = File.OpenRead(ZipPath);
        var run1 = CoastalFinancialLinkService.Execute(zipStream1, years, facts, catalog);

        // Verify entities were NOT mutated by Execute
        Assert.All(years, y => Assert.Equal(0, y.FinancialId));
        Assert.All(facts, f => Assert.Equal(0, f.FinancialFactId));

        using var zipStream2 = File.OpenRead(ZipPath);
        var run2 = CoastalFinancialLinkService.Execute(zipStream2, years, facts, catalog);

        // Verify repeatability: runs 1 and 2 are identical
        Assert.Equal(run1.Entries.Count, run2.Entries.Count);
        for (var i = 0; i < run1.Entries.Count; i++)
        {
            var e1 = run1.Entries[i];
            var e2 = run2.Entries[i];

            Assert.Equal(e1.OuterEntryFullPath, e2.OuterEntryFullPath);
            Assert.Equal(e1.NestedEntryRelativePath, e2.NestedEntryRelativePath);
            Assert.Equal(e1.Sha256Hex, e2.Sha256Hex);
            Assert.Equal(e1.Outcome, e2.Outcome);
            Assert.Equal(e1.Reason, e2.Reason);
            Assert.Equal(e1.MatchedFinancialYear, e2.MatchedFinancialYear);
            Assert.Equal(e1.MatchedBasis, e2.MatchedBasis);
            Assert.Equal(e1.TargetKind, e2.TargetKind);
            Assert.Equal(e1.TargetEntityId, e2.TargetEntityId);
            Assert.Equal(e1.TargetLineItem, e2.TargetLineItem);
            Assert.Equal(e1.MatchedValue, e2.MatchedValue);
            Assert.Equal(e1.EvidencePageNumber, e2.EvidencePageNumber);
            Assert.Equal(e1.EvidenceTextQuote, e2.EvidenceTextQuote);
        }
    }

    [SkippableFact]
    public void VerifyLiveExecution_MatchesBaselineFixture()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath), "Coastal files not present.");

        var (years, facts, catalog) = LoadCoastalFinancialData();

        using var zipStream = File.OpenRead(ZipPath);
        var liveResult = CoastalFinancialLinkService.Execute(zipStream, years, facts, catalog);

        var orderedLive = liveResult.Entries
            .OrderBy(e => e.OuterEntryFullPath, StringComparer.Ordinal)
            .ThenBy(e => e.NestedEntryRelativePath, StringComparer.Ordinal)
            .ToList();

        var baselineEntries = LoadBaselineEntries();

        Assert.Equal(baselineEntries.Count, orderedLive.Count);

        for (var i = 0; i < baselineEntries.Count; i++)
        {
            var baseEntry = baselineEntries[i];
            var liveEntry = orderedLive[i];

            Assert.Equal(baseEntry.OuterEntryFullPath, liveEntry.OuterEntryFullPath);
            Assert.Equal(baseEntry.NestedEntryRelativePath, liveEntry.NestedEntryRelativePath);
            Assert.Equal(baseEntry.Sha256Hex, liveEntry.Sha256Hex);
            Assert.Equal(baseEntry.Outcome, liveEntry.Outcome);
            Assert.Equal(baseEntry.Reason, liveEntry.Reason);
            Assert.Equal(baseEntry.MatchedFinancialYear, liveEntry.MatchedFinancialYear);
            Assert.Equal(baseEntry.MatchedBasis, liveEntry.MatchedBasis);
            Assert.Equal(baseEntry.TargetKind, liveEntry.TargetKind);
            Assert.Equal(baseEntry.TargetEntityId, liveEntry.TargetEntityId);
            Assert.Equal(baseEntry.TargetLineItem, liveEntry.TargetLineItem);
            Assert.Equal(baseEntry.MatchedValue, liveEntry.MatchedValue);
            Assert.Equal(baseEntry.EvidencePageNumber, liveEntry.EvidencePageNumber);
            Assert.Equal(baseEntry.EvidenceTextQuote, liveEntry.EvidenceTextQuote);
            Assert.Equal(baseEntry.IsCanonical, liveEntry.IsCanonical);
            Assert.Equal(baseEntry.CanonicalOuterEntryFullPath, liveEntry.CanonicalOuterEntryFullPath);
            Assert.Equal(baseEntry.CanonicalNestedEntryRelativePath, liveEntry.CanonicalNestedEntryRelativePath);
        }
    }
}
