using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Real .\SQLEXPRESS test DB (this builder is DB-driven, not DossierModel-driven — see its own
/// class doc for why). Every test seeds its own isolated request/ingestion run.</summary>
public class CorporateTimelineBuilderTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string? RocFixture()
    {
        var dir = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures", "workbooks");
        var roc = Path.Combine(dir, "roc.xls");
        return File.Exists(roc) ? roc : null;
    }

    /// <summary>Creates an isolated request + ingestion run (NO AnalysisRun, unless the caller adds one)
    /// and returns (RequestId, IngestionRunId). Stamp lineage on seeded rows with <see cref="Tag{E}"/>.</summary>
    private static async Task<(long RequestId, long IngestionRunId)> SeedRequestAsync(
        AppDbContext db, string companyName = "Timeline Test Co")
    {
        var client = new Client { ClientCode = $"T{Guid.NewGuid():N}"[..10], ClientName = "Test Bank", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = companyName,
            Cin = "U12345KA2000PLC000099", Pan = "AAAAA0000B",
            RequestNumber = $"TL-{Guid.NewGuid():N}", RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var ing = new IngestionRun
        {
            RequestId = request.RequestId, RunNumber = 1, StartedDate = request.CreatedDate,
            CompletedDate = request.CreatedDate.AddMinutes(2), Status = IngestionRunStatus.CompletedClean
        };
        db.IngestionRuns.Add(ing);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = ing.IngestionRunId;
        await db.SaveChangesAsync();

        return (request.RequestId, ing.IngestionRunId);
    }

    private static E Tag<E>(E e, long requestId, long ingestionRunId) where E : ExtractedEntityBase
    {
        e.RequestId = requestId;
        e.IngestionRunId = ingestionRunId;
        return e;
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_completed_ingestion_run()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = $"T{Guid.NewGuid():N}"[..10], ClientName = "Test Bank", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "No Ingestion Co",
            RequestNumber = $"TL-{Guid.NewGuid():N}", RequestStatus = RequestStatus.ExtractionInProgress,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(request.RequestId);
        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_events_with_zero_completed_analysis_runs()
    {
        // The exact regression this design avoids: DossierAssembler requires a completed AnalysisRun
        // before loading anything, so a DossierModel-based timeline would return nothing here. This
        // builder must not — no AnalysisRun exists anywhere in this test's seed data.
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.CompanyProfiles.Add(Tag(new CompanyProfile { CompanyName = "Timeline Test Co", IncorporationDate = new DateOnly(2010, 4, 1) }, requestId, ingestionRunId));
        await db.SaveChangesAsync();
        Assert.Equal(0, await db.AnalysisRuns.CountAsync(a => a.RequestId == requestId));

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Company incorporated", result[0].Title);
    }

    [Fact]
    public async Task Incorporation_event_is_dated_and_provenanced()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        var profile = Tag(new CompanyProfile { CompanyName = "Timeline Test Co", IncorporationDate = new DateOnly(2010, 4, 1), SourceSheetName = "About the Company", SourceRowNumber = 5 }, requestId, ingestionRunId);
        db.CompanyProfiles.Add(profile);
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        var e = Assert.Single(result!);
        Assert.Equal(new DateOnly(2010, 4, 1), e.Date);
        Assert.Equal(TimelineEventCategory.Corporate, e.Category);
        Assert.Equal(nameof(CompanyProfile), e.Provenance.EntityType);
        Assert.Equal(profile.CompanyProfileId, e.Provenance.EntityId);
        Assert.Equal("About the Company", e.Provenance.SourceSheetName);
        Assert.Equal(5, e.Provenance.SourceRowNumber);
    }

    [Fact]
    public async Task Name_change_chains_to_the_next_historical_name_and_the_final_row_chains_to_the_current_name()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db, "Current Name Ltd");
        db.CompanyNameHistories.AddRange(
            Tag(new CompanyNameHistory { PreviousName = "First Name Ltd", TillDate = new DateOnly(2015, 1, 1), DisplayOrder = 1 }, requestId, ingestionRunId),
            Tag(new CompanyNameHistory { PreviousName = "Second Name Ltd", TillDate = new DateOnly(2020, 1, 1), DisplayOrder = 2 }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.Equal(2, result!.Count);
        Assert.Equal("Renamed from \"First Name Ltd\" to \"Second Name Ltd\"", result[0].Title);
        Assert.Equal("Renamed from \"Second Name Ltd\" to \"Current Name Ltd\"", result[1].Title);
    }

    [Fact]
    public async Task Director_events_come_from_assignment_history_stints_when_present()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.Directors.Add(Tag(new Director { Din = "111", NameRaw = "ALICE RAO", Designation = "Director", OriginalAppointmentDate = new DateOnly(2000, 1, 1) }, requestId, ingestionRunId));
        db.DirectorAssignmentHistories.AddRange(
            Tag(new DirectorAssignmentHistory { DirectorDin = "111", DirectorNameRaw = "ALICE RAO", Designation = "Director", AppointmentDate = new DateOnly(2010, 1, 1), CessationDate = new DateOnly(2015, 1, 1) }, requestId, ingestionRunId),
            Tag(new DirectorAssignmentHistory { DirectorDin = "111", DirectorNameRaw = "ALICE RAO", Designation = "Managing Director", AppointmentDate = new DateOnly(2015, 1, 2) }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        // The assignment-history stints are used, NOT Director.OriginalAppointmentDate (2000) — 3 events total.
        Assert.Equal(3, result!.Count);
        Assert.DoesNotContain(result, e => e.Date == new DateOnly(2000, 1, 1));
        Assert.Contains(result, e => e.Title == "ALICE RAO appointed as Director" && e.Date == new DateOnly(2010, 1, 1));
        Assert.Contains(result, e => e.Title == "ALICE RAO ceased as Director" && e.Date == new DateOnly(2015, 1, 1));
        Assert.Contains(result, e => e.Title == "ALICE RAO appointed as Managing Director" && e.Date == new DateOnly(2015, 1, 2));
    }

    [Fact]
    public async Task Director_falls_back_to_its_own_dates_when_no_assignment_history_rows_exist_for_that_DIN()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.Directors.Add(Tag(new Director { Din = "222", NameRaw = "BOB SEN", Designation = "Director", OriginalAppointmentDate = new DateOnly(2018, 6, 1), CessationDate = new DateOnly(2022, 3, 1) }, requestId, ingestionRunId));
        // No DirectorAssignmentHistory rows at all for DIN 222.
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, e => e.Title == "BOB SEN appointed as Director" && e.Date == new DateOnly(2018, 6, 1));
        Assert.Contains(result, e => e.Title == "BOB SEN ceased as Director" && e.Date == new DateOnly(2022, 3, 1));
        Assert.All(result, e => Assert.Equal(nameof(Director), e.Provenance.EntityType));
    }

    [Fact]
    public async Task Charge_events_carry_holder_and_amount_in_the_detail()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.RocChargeEvents.Add(Tag(new RocChargeEvent
        {
            RocChargeId = 1, EventType = ChargeEventType.Creation, EventDate = new DateOnly(2016, 3, 18),
            ChargeAmount = 610m, HolderNameRaw = "STATE BANK OF INDIA"
        }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        var e = Assert.Single(result!);
        Assert.Equal(TimelineEventCategory.Charges, e.Category);
        Assert.Contains("STATE BANK OF INDIA", e.Title);
        Assert.Contains("610", e.Detail);
    }

    [Fact]
    public async Task Capital_events_are_emitted_for_security_allotments()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.SecurityAllotments.Add(Tag(new SecurityAllotment { AllotmentDate = new DateOnly(2019, 5, 1), AllotmentType = "Rights Issue", NumberOfSecurities = 10000, AmountCrore = 5m }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        var e = Assert.Single(result!);
        Assert.Equal(TimelineEventCategory.Capital, e.Category);
        Assert.Equal(new DateOnly(2019, 5, 1), e.Date);
        Assert.Contains("Rights Issue", e.Title);
    }

    [Fact]
    public async Task Credit_rating_events_label_unaccepted_ratings()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.CreditRatings.AddRange(
            Tag(new CreditRating { Agency = "CRISIL", RatingDate = new DateOnly(2021, 1, 1), Instrument = "Term Loan", Rating = "BBB", IsAccepted = true }, requestId, ingestionRunId),
            Tag(new CreditRating { Agency = "ICRA", RatingDate = new DateOnly(2022, 1, 1), Instrument = "Cash Credit", Rating = "BB", IsAccepted = false }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.Equal(2, result!.Count);
        Assert.DoesNotContain("not accepted", result[0].Title);
        Assert.Contains("not accepted", result[1].Title);
    }

    [Fact]
    public async Task Financial_dispute_default_and_judgement_are_two_separate_events()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.FinancialDisputeCases.Add(Tag(new FinancialDisputeCase
        {
            DateOfDefault = new DateOnly(2018, 1, 1), DateOfJudgement = new DateOnly(2020, 1, 1),
            AmountUnderDefault = 12m, DisputeType = "Money claim", Verdict = "Decreed"
        }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, e => e.Title == "Financial dispute default" && e.Date == new DateOnly(2018, 1, 1));
        Assert.Contains(result, e => e.Title == "Financial dispute judgement" && e.Date == new DateOnly(2020, 1, 1) && e.Detail == "Decreed");
    }

    [Fact]
    public async Task Gst_registration_and_cancellation_are_two_separate_events()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.GstRegistrations.Add(Tag(new GstRegistration { Gstin = "29ABCDE1234F1Z5", RegistrationDate = new DateOnly(2017, 7, 1), CancellationDate = new DateOnly(2021, 3, 31), State = "Karnataka" }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, e => e.Title.Contains("registered") && e.Date == new DateOnly(2017, 7, 1));
        Assert.Contains(result, e => e.Title.Contains("cancelled") && e.Date == new DateOnly(2021, 3, 31));
    }

    [Fact]
    public async Task Epfo_establishment_setup_event()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.EpfoEstablishments.Add(Tag(new EpfoEstablishment { EstablishmentId = "KA/BLR/12345", Name = "Head Office", DateOfSetup = new DateOnly(2005, 8, 15) }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        var e = Assert.Single(result!);
        Assert.Equal(TimelineEventCategory.Epfo, e.Category);
        Assert.Equal(new DateOnly(2005, 8, 15), e.Date);
    }

    [Fact]
    public async Task Compliance_record_event_carries_defaulter_detail()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        db.ComplianceRecords.Add(Tag(new ComplianceRecord
        {
            RecordType = ComplianceRecordType.SuitFiled, RecordDate = new DateOnly(2019, 11, 1),
            Source = "CIBIL", Bank = "State Bank of India", AmountCrore = 25m, DefaulterType = "Defaulter - Suit Filed"
        }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        var e = Assert.Single(result!);
        Assert.Equal(TimelineEventCategory.Compliance, e.Category);
        Assert.Contains("Defaulter - Suit Filed", e.Detail);
        Assert.Contains("State Bank of India", e.Detail);
    }

    [Fact]
    public async Task Same_day_events_are_ordered_by_category_then_by_source_sheet_and_row_as_a_stable_tie_breaker()
    {
        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db);
        var sameDay = new DateOnly(2020, 1, 1);
        db.SecurityAllotments.Add(Tag(new SecurityAllotment { AllotmentDate = sameDay, SourceSheetName = "Securities Allotment", SourceRowNumber = 9 }, requestId, ingestionRunId));
        db.EpfoEstablishments.Add(Tag(new EpfoEstablishment { EstablishmentId = "E1", DateOfSetup = sameDay, SourceSheetName = "EPFO Establishments", SourceRowNumber = 2 }, requestId, ingestionRunId));
        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.Equal(2, result!.Count);
        // TimelineEventCategory declares Corporate, Directors, Charges, Capital, CreditRatings, Gst,
        // Epfo, Compliance, FinancialDisputes — Capital (index 3) sorts before Epfo (index 6)
        // regardless of source-sheet name, since Category is the sort key ahead of provenance.
        Assert.Equal(TimelineEventCategory.Capital, result[0].Category);
        Assert.Equal(TimelineEventCategory.Epfo, result[1].Category);
    }

    [SkippableFact]
    public async Task Coastal_fixture_exact_timeline_shape()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        await using var db = CreateContext();
        var (requestId, ingestionRunId) = await SeedRequestAsync(db, "COASTAL PROJECTS LIMITED");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);

        var aboutSheet = sheets.Single(s => s.Name == "About the Company");
        var profile = CompanyProfileParser.Parse(aboutSheet, 1, 1, 1).Items.Single();
        db.CompanyProfiles.Add(Tag(profile, requestId, ingestionRunId));

        var highlightsSheet = sheets.Single(s => s.Name == "Highlights");
        var nameHistory = HighlightsParser.ParseNameHistory(highlightsSheet, 1, 1, 1).Items;
        db.CompanyNameHistories.AddRange(nameHistory.Select(x => Tag(x, requestId, ingestionRunId)));

        var directorSheet = sheets.Single(s => s.Name == "Directors");
        var directors = DirectorsParser.Parse(directorSheet, 1, 1, 1, out _).Items;
        db.Directors.AddRange(directors.Select(x => Tag(x, requestId, ingestionRunId)));

        var assocHistorySheet = sheets.SingleOrDefault(s => s.Name == "Director - Association History");
        if (assocHistorySheet is not null)
        {
            var assocHistory = DirectorAssociationHistoryParser.Parse(assocHistorySheet, 1, 1, 1).Items;
            db.DirectorAssignmentHistories.AddRange(assocHistory.Select(x => Tag(x, requestId, ingestionRunId)));
        }

        await db.SaveChangesAsync();

        var result = await new CorporateTimelineBuilder(db).BuildAsync(requestId);
        Assert.NotNull(result);
        Assert.NotEmpty(result);

        // Incorporation is always the earliest Corporate event, and the very first event overall since
        // nothing else in this fixture predates a company's own incorporation date.
        var incorporation = Assert.Single(result!, e => e.Title == "Company incorporated");
        Assert.Equal(profile.IncorporationDate, incorporation.Date);
        Assert.Equal(result![0], incorporation);
    }
}
