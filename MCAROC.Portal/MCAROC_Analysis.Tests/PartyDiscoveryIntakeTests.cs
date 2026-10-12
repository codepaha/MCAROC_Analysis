using System.Net;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Discovery;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class PartyDiscoveryIntakeTests : IAsyncLifetime
{
    private static AppDbContext Ctx() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<McaRequest> CreateRequestAsync(string companyName = "Acme Technologies Limited")
    {
        await using var db = Ctx();
        var client = new Client
        {
            ClientCode = "C" + Guid.NewGuid().ToString("N")[..8],
            ClientName = "Client " + Guid.NewGuid().ToString("N")[..6],
            CreatedDate = DateTime.UtcNow
        };
        db.Clients.Add(client);

        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = companyName,
            Cin = "U" + Guid.NewGuid().ToString("N")[..20],
            RequestNumber = "REQ-" + Guid.NewGuid().ToString("N")[..8],
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow,
            LiveDataVersion = 0
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static DiscoveredCaseCandidate CreateCandidate(
        string caseNumber,
        string sourceVenueId,
        string applicant,
        string respondent,
        DiscoveredCaseSource source = DiscoveredCaseSource.Drt,
        string venueName = "DRT 1 Delhi",
        string? diaryNo = null)
    {
        var normalized = CaseNumberNormaliser.Parse(new CaseNumberInput(caseNumber));
        var match = WholeNamePartyMatcher.Match(applicant, ["Acme Technologies Limited"], []);
        if (match.Outcome != PartyMatchOutcome.Verified)
            match = WholeNamePartyMatcher.Match(respondent, ["Acme Technologies Limited"], []);

        return new DiscoveredCaseCandidate(
            Source: source,
            SourceVenueId: sourceVenueId,
            SourceVenueName: venueName,
            CaseNumber: caseNumber,
            NormalizedCaseNumber: normalized,
            FilingDate: new DateOnly(2022, 5, 1),
            ApplicantOrComplainant: applicant,
            Respondent: respondent,
            MatchResult: match,
            QueriedPartyName: "Acme Technologies Limited",
            IsAccepted: match.Outcome == PartyMatchOutcome.Verified,
            DiaryNo: diaryNo);
    }

    private static DrtCaseDetails CreateDrtCaseDetails(
        int tribunalId,
        string caseNumber,
        string applicant,
        string respondent,
        string status = "PENDING",
        DateOnly? nextListing = null,
        DateOnly? disposalDate = null,
        string? disposalNature = null,
        IReadOnlyList<DrtHearing>? hearings = null)
    {
        var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(caseNumber));
        return new DrtCaseDetails(
            TribunalId: tribunalId,
            CaseTypeId: 1,
            CaseType: parts.TypeCode ?? "OA",
            CaseTypeCode: parts.TypeCode ?? "OA",
            CaseNo: parts.No ?? "1",
            CaseYear: parts.Year ?? 2022,
            CaseNumber: parts,
            DiaryNo: $"{parts.No}/{parts.Year}",
            DiaryYear: parts.Year,
            FilingNo: null,
            DateOfFiling: new DateOnly(2022, 1, 1),
            Status: status,
            StatusParts: CaseStatusVocabulary.Map(status),
            StatusCode: status,
            DateOfDisposal: disposalDate,
            DisposalNature: disposalNature,
            NextListingDate: nextListing,
            NextListingPurpose: "Hearing",
            CourtNo: "1",
            CourtName: $"Debts Recovery Tribunal {tribunalId}",
            MainCaseCaseNo: null,
            SuitAmount: null,
            PetitionerName: applicant,
            RespondentName: respondent,
            PetitionerAddress: null,
            RespondentAddress: null,
            PetitionerAdvocate: null,
            RespondentAdvocate: null,
            AdditionalPetitioner: null,
            AdditionalRespondent: null,
            Hearings: hearings ?? [],
            IaDetails: null);
    }

    [Fact]
    public async Task Cooperative_society_union_and_group_company_negatives_are_rejected_and_not_added()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        var coopCandidate = CreateCandidate(
            "OA/101/2022", "1",
            applicant: "Acme Technologies Limited Employees Cooperative Society",
            respondent: "Some Borrower Ltd");

        var unionCandidate = CreateCandidate(
            "OA/102/2022", "1",
            applicant: "Some Bank",
            respondent: "Acme Technologies Limited Employees Union");

        var groupCandidate = CreateCandidate(
            "OA/103/2022", "1",
            applicant: "Acme Technologies Limited Securities",
            respondent: "Other Entity");

        await using var db = Ctx();
        var drtMock = new MockDrtIntakeClient();
        var consumerMock = new MockConsumerIntakeClient();
        var service = new PartyDiscoveryIntakeService(db, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);

        var result = await service.IntakeCandidatesAsync(
            req.RequestId,
            companyNames: ["Acme Technologies Limited"],
            formerNames: null,
            candidates: [coopCandidate, unionCandidate, groupCandidate]);

        Assert.Equal(3, result.TotalCandidatesProcessed);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(3, result.RejectedCount);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(IntakeCandidateStatus.PartyRejected, item.Status);
            Assert.Null(item.LegacyLitigationCaseId);
            Assert.Null(item.ModuleCaseId);
        });

        // Assert nothing was added to the database
        var count = await db.LitigationCases.CountAsync(c => c.RequestId == req.RequestId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Partial_number_candidate_fails_gate_and_is_logged_not_keyed_or_added()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        // "CONTEMPT PETITION" is an unknown type not in dictionary -> fails gate
        var partialCandidate = CreateCandidate(
            "88/2014", "1",
            applicant: "Acme Technologies Limited",
            respondent: "Borrower Ltd");

        // Force a partial parsed number
        partialCandidate = partialCandidate with
        {
            NormalizedCaseNumber = CaseNumberNormaliser.Parse(new CaseNumberInput("88", CaseType: "CONTEMPT PETITION", CaseYear: "2014"))
        };

        await using var db = Ctx();
        var drtMock = new MockDrtIntakeClient();
        var consumerMock = new MockConsumerIntakeClient();
        var service = new PartyDiscoveryIntakeService(db, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);

        var result = await service.IntakeCandidatesAsync(
            req.RequestId,
            companyNames: ["Acme Technologies Limited"],
            formerNames: null,
            candidates: [partialCandidate]);

        Assert.Equal(1, result.TotalCandidatesProcessed);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.PossibleDuplicatesCount);

        var item = Assert.Single(result.Items);
        Assert.Equal(IntakeCandidateStatus.FailedGatePartial, item.Status);
        Assert.Null(item.CaseKey);
        Assert.Null(item.LegacyLitigationCaseId);

        var count = await db.LitigationCases.CountAsync(c => c.RequestId == req.RequestId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Candidate_matching_existing_case_by_CaseKey_is_not_duplicated()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        // Seed existing case with CaseKey
        await using (var dbSeed = Ctx())
        {
            var existingCase = new LitigationCase
            {
                RequestId = req.RequestId,
                CaseNumber = "OA/413/2022",
                CaseType = "OA",
                CaseYear = "2022",
                Court = "Debts Recovery Tribunal Delhi (DRT 1)",
                CaseStatus = "PENDING",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            };
            dbSeed.LitigationCases.Add(existingCase);
            await dbSeed.SaveChangesAsync();

            var moduleCase = await new LitigationModuleStore(dbSeed).EnsureCaseForLegacyAsync(existingCase.LitigationCaseId, default);
            moduleCase.CaseKey = "DRT|1|OA|413|2022";
            await dbSeed.SaveChangesAsync();
        }

        var candidate = CreateCandidate(
            "OA/413/2022", "1",
            applicant: "Acme Technologies Limited",
            respondent: "Defaulting Borrower");

        await using var db = Ctx();
        var drtMock = new MockDrtIntakeClient();
        var consumerMock = new MockConsumerIntakeClient();
        var service = new PartyDiscoveryIntakeService(db, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);

        var result = await service.IntakeCandidatesAsync(
            req.RequestId,
            companyNames: ["Acme Technologies Limited"],
            formerNames: null,
            candidates: [candidate]);

        Assert.Equal(1, result.TotalCandidatesProcessed);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.AlreadyExistingCount);

        var item = Assert.Single(result.Items);
        Assert.Equal(IntakeCandidateStatus.AlreadyExists, item.Status);
        Assert.Equal("DRT|1|OA|413|2022", item.CaseKey);

        // Ensure database still has only 1 case row
        var count = await db.LitigationCases.CountAsync(c => c.RequestId == req.RequestId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Genuinely_new_case_creates_case_row_module_case_link_observation_and_publishes()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        var candidate = CreateCandidate(
            "OA/501/2022", "1",
            applicant: "Acme Technologies Limited",
            respondent: "New Borrower Ltd",
            diaryNo: "501/2022");

        var drtMock = new MockDrtIntakeClient();
        drtMock.SetupCase(1, 1, "501", 2022, CreateDrtCaseDetails(
            1, "OA/501/2022", "Acme Technologies Limited", "New Borrower Ltd",
            status: "PENDING",
            nextListing: new DateOnly(2026, 11, 20),
            hearings: [new DrtHearing(new DateOnly(2026, 1, 15), "Hearing", "Pending", null, null, null, null)]));

        await using var db = Ctx();
        var consumerMock = new MockConsumerIntakeClient();
        var service = new PartyDiscoveryIntakeService(db, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);

        var result = await service.IntakeCandidatesAsync(
            req.RequestId,
            companyNames: ["Acme Technologies Limited"],
            formerNames: null,
            candidates: [candidate]);

        Assert.Equal(1, result.CreatedCount);
        var item = Assert.Single(result.Items);
        Assert.Equal(IntakeCandidateStatus.Created, item.Status);
        Assert.NotNull(item.LegacyLitigationCaseId);
        Assert.NotNull(item.ModuleCaseId);
        Assert.False(item.IsDisposed);
        Assert.Equal("DRT|1|OA|501|2022", item.CaseKey);

        // Verify entity state in DB
        var caseRow = await db.LitigationCases.FirstOrDefaultAsync(c => c.LitigationCaseId == item.LegacyLitigationCaseId);
        Assert.NotNull(caseRow);
        Assert.Equal("OA/501/2022", caseRow.CaseNumber);
        Assert.Equal("PENDING", caseRow.CaseStatus);
        Assert.Equal("20-11-2026", caseRow.NextHearingDate);

        var moduleCase = await db.Set<ModuleCase>().FirstOrDefaultAsync(m => m.CaseId == item.ModuleCaseId);
        Assert.NotNull(moduleCase);
        Assert.Equal("DRT|1|OA|501|2022", moduleCase.CaseKey);

        var link = await db.Set<ModuleRequestLink>().FirstOrDefaultAsync(l => l.RequestId == req.RequestId && l.CaseId == item.ModuleCaseId);
        Assert.NotNull(link);
        Assert.Equal((int)PartyMatchOutcome.Verified, link.Verdict);

        var obs = await db.Set<ModuleCaseObservation>().FirstOrDefaultAsync(o => o.CaseId == item.ModuleCaseId);
        Assert.NotNull(obs);
        Assert.True(obs.Verified);
        Assert.Equal((int)ObservationOutcome.Found, obs.Outcome);
    }

    [Fact]
    public async Task New_disposed_case_routes_to_orders_path_only_and_is_not_scheduled_for_refresh()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        var candidate = CreateCandidate(
            "OA/601/2020", "1",
            applicant: "Acme Technologies Limited",
            respondent: "Old Borrower Ltd");

        var drtMock = new MockDrtIntakeClient();
        drtMock.SetupCase(1, 1, "601", 2020, CreateDrtCaseDetails(
            1, "OA/601/2020", "Acme Technologies Limited", "Old Borrower Ltd",
            status: "DISPOSED",
            disposalDate: new DateOnly(2022, 6, 1),
            disposalNature: "Decreed with costs",
            hearings: [new DrtHearing(new DateOnly(2022, 6, 1), "Final Hearing", "Disposed", null, null, null, null)]));

        await using var db = Ctx();
        var consumerMock = new MockConsumerIntakeClient();
        var service = new PartyDiscoveryIntakeService(db, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);

        var result = await service.IntakeCandidatesAsync(
            req.RequestId,
            companyNames: ["Acme Technologies Limited"],
            formerNames: null,
            candidates: [candidate]);

        Assert.Equal(1, result.CreatedCount);
        var item = Assert.Single(result.Items);
        Assert.Equal(IntakeCandidateStatus.Created, item.Status);
        Assert.True(item.IsDisposed);
        // Routed to orders only, never scheduled for hearing refresh
        Assert.Equal(LiveRefreshAction.FetchOrdersOnly, item.RefreshAction);
    }

    [Fact]
    public async Task Two_simultaneous_discoveries_of_the_same_case_create_one_row_under_CaseLock()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        var candidate = CreateCandidate(
            "OA/777/2021", "1",
            applicant: "Acme Technologies Limited",
            respondent: "Race Condition Borrower");

        var drtMock = new MockDrtIntakeClient();
        drtMock.SetupCase(1, 1, "777", 2021, CreateDrtCaseDetails(
            1, "OA/777/2021", "Acme Technologies Limited", "Race Condition Borrower",
            status: "PENDING",
            nextListing: new DateOnly(2026, 10, 25)));

        var consumerMock = new MockConsumerIntakeClient();

        // Run two concurrent discovery intake tasks simultaneously
        var task1 = Task.Run(async () =>
        {
            await using var db1 = Ctx();
            var service1 = new PartyDiscoveryIntakeService(db1, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);
            return await service1.IntakeCandidatesAsync(req.RequestId, ["Acme Technologies Limited"], null, [candidate]);
        });

        var task2 = Task.Run(async () =>
        {
            await using var db2 = Ctx();
            var service2 = new PartyDiscoveryIntakeService(db2, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);
            return await service2.IntakeCandidatesAsync(req.RequestId, ["Acme Technologies Limited"], null, [candidate]);
        });

        var results = await Task.WhenAll(task1, task2);
        var totalCreated = results.Sum(r => r.CreatedCount);
        var totalAlreadyExists = results.Sum(r => r.AlreadyExistingCount);

        Assert.Equal(1, totalCreated);
        Assert.Equal(1, totalAlreadyExists);

        // Verify only 1 row exists in the database
        await using var dbVerify = Ctx();
        var storedCount = await dbVerify.LitigationCases.CountAsync(c => c.RequestId == req.RequestId && c.CaseNumber == "OA/777/2021");
        Assert.Equal(1, storedCount);
    }

    [Fact]
    public async Task Batch_intake_bumps_LiveDataVersion_once_for_the_batch_not_per_case()
    {
        var req = await CreateRequestAsync("Acme Technologies Limited");

        var c1 = CreateCandidate("OA/801/2022", "1", "Acme Technologies Limited", "Borrower A");
        var c2 = CreateCandidate("OA/802/2022", "1", "Acme Technologies Limited", "Borrower B");

        var drtMock = new MockDrtIntakeClient();
        drtMock.SetupCase(1, 1, "801", 2022, CreateDrtCaseDetails(1, "OA/801/2022", "Acme Technologies Limited", "Borrower A", status: "PENDING"));
        drtMock.SetupCase(1, 1, "802", 2022, CreateDrtCaseDetails(1, "OA/802/2022", "Acme Technologies Limited", "Borrower B", status: "PENDING"));

        await using var db = Ctx();
        var consumerMock = new MockConsumerIntakeClient();
        var service = new PartyDiscoveryIntakeService(db, drtMock, consumerMock, NullLogger<PartyDiscoveryIntakeService>.Instance);

        var versionBefore = await RequestLiveData.ReadAsync(db, req.RequestId, default);
        Assert.Equal(0, versionBefore);

        var result = await service.IntakeCandidatesAsync(req.RequestId, ["Acme Technologies Limited"], null, [c1, c2]);

        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(1, result.NewLiveDataVersion);

        var versionAfter = await RequestLiveData.ReadAsync(db, req.RequestId, default);
        // Incremented exactly once (0 -> 1)
        Assert.Equal(1, versionAfter);
    }

    private sealed class MockDrtIntakeClient : IDrtClient
    {
        private readonly Dictionary<(int TribunalId, int CaseTypeId, string CaseNo, int CaseYear), DrtCaseDetails> _cases = [];

        public string CurrentExit => "127.0.0.1:8080";

        public void SetupCase(int tribunalId, int caseTypeId, string caseNo, int caseYear, DrtCaseDetails details)
        {
            _cases[(tribunalId, caseTypeId, caseNo, caseYear)] = details;
        }

        public Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtTribunal>>([new(1, "Debts Recovery Tribunal Delhi (DRT 1)", false, "Delhi", "Delhi", "DRT 1")]);

        public Task<IReadOnlyList<DrtCaseType>> GetCaseTypesAsync(int tribunalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtCaseType>>([]);

        public Task<IReadOnlyList<DrtPartySearchResult>> SearchByPartyNameAsync(int tribunalId, string partyName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtPartySearchResult>>([]);

        public Task<DrtCaseLookupResult> GetCaseByNumberAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
        {
            if (_cases.TryGetValue((tribunalId, caseTypeId, caseNo, caseYear), out var details))
            {
                return Task.FromResult(DrtCaseLookupResult.Found(details));
            }
            return Task.FromResult(DrtCaseLookupResult.Error("Not found in mock", null, HttpStatusCode.NotFound));
        }

        public Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not found", null, HttpStatusCode.NotFound));

        public Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtOrder>>([]);

        public Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtOrder>>([]);

        public Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20971520, CancellationToken ct = default) =>
            Task.FromResult(DrtOrderDownloadResult.NotAvailable());
    }

    private sealed class MockConsumerIntakeClient : IConsumerClient
    {
        public string CurrentExit => "127.0.0.1:8080";
        public ConsumerProxyRotator Rotator => new([]);

        public Task<ConsumerCaseLookupResult> GetCaseStatusAsync(string caseNumber, CancellationToken ct = default) =>
            Task.FromResult(ConsumerCaseLookupResult.Error("Not found in mock", null, HttpStatusCode.NotFound));

        public Task<ConsumerCompanySearchResult> SearchByCompanyAsync(int commissionTypeId, int commissionId, string keyword, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default) =>
            Task.FromResult(new ConsumerCompanySearchResult([], true));

        public Task<ConsumerOrderDownloadResult> GetDailyOrderPdfAsync(long filingReferenceNumber, DateOnly dateOfHearing, int orderTypeId = 1, CancellationToken ct = default) =>
            Task.FromResult(ConsumerOrderDownloadResult.NotFound());
    }
}

