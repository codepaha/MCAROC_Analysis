using System.Net;
using MCAROC_Analysis.Services.LitigationModule.Discovery;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class PartyDiscoverySearchServiceTests
{
    [Fact]
    public async Task DiscoverDrtCandidatesAsync_queries_former_names_as_well_as_current_names()
    {
        var drt = new MockDrtDiscoveryClient();
        var service = new PartyDiscoverySearchService(drt, new MockConsumerDiscoveryClient(), NullLogger<PartyDiscoverySearchService>.Instance);
        await service.DiscoverDrtCandidatesAsync(["New Bank Limited"], ["Old Bank Limited"], [1]);
        Assert.Contains("Old Bank Limited", drt.Queries);
        Assert.Contains("New Bank Limited", drt.Queries);
    }

    [Fact]
    public async Task DiscoverConsumerCandidatesAsync_queries_former_names_as_well_as_current_names()
    {
        var consumer = new MockConsumerDiscoveryClient();
        var service = new PartyDiscoverySearchService(new MockDrtDiscoveryClient(), consumer, NullLogger<PartyDiscoverySearchService>.Instance);
        await service.DiscoverConsumerCandidatesAsync(["New Bank Limited"], ["Old Bank Limited"], [(1, 11000000)]);
        Assert.Contains("Old Bank Limited", consumer.Queries);
        Assert.Contains("New Bank Limited", consumer.Queries);
    }

    [Fact]
    public async Task DiscoverDrtCandidatesAsync_preserves_distinct_keyless_observations()
    {
        var drt = new MockDrtDiscoveryClient();
        foreach (var diary in new[] { "100/2020", "200/2021" })
        {
            drt.AddResult(1, new DrtPartySearchResult(1, "OA", null,
                CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: null)), diary,
                null, "New Bank Limited", "Borrower", null, null, null));
        }
        var service = new PartyDiscoverySearchService(drt, new MockConsumerDiscoveryClient(), NullLogger<PartyDiscoverySearchService>.Instance);
        var result = await service.DiscoverDrtCandidatesAsync(["New Bank Limited"], tribunalIds: [1]);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task DiscoverDrtCandidatesAsync_accepts_whole_name_matches_and_rejects_extra_words_with_audit()
    {
        var drtMock = new MockDrtDiscoveryClient();

        // 1. Exact match candidate
        drtMock.AddResult(1, new DrtPartySearchResult(
            TribunalId: 1,
            RawCaseType: "OA",
            RawCaseNo: "OA/100/2020",
            CaseNumber: CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: "OA/100/2020")),
            DiaryNo: "100/2020",
            DateOfFiling: new DateOnly(2020, 1, 1),
            Applicant: "STATE BANK OF INDIA",
            Respondent: "BORROWER PVT LTD",
            PetitionerAdvocate: null,
            RespondentAdvocate: null,
            FilingNo: null));

        // 2. Extra words candidate: cooperative society negative test
        drtMock.AddResult(1, new DrtPartySearchResult(
            TribunalId: 1,
            RawCaseType: "OA",
            RawCaseNo: "OA/101/2020",
            CaseNumber: CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: "OA/101/2020")),
            DiaryNo: "101/2020",
            DateOfFiling: new DateOnly(2020, 2, 1),
            Applicant: "STATE BANK OF INDIA EMPLOYEES COOPERATIVE SOCIETY",
            Respondent: "BORROWER PVT LTD",
            PetitionerAdvocate: null,
            RespondentAdvocate: null,
            FilingNo: null));

        var consumerMock = new MockConsumerDiscoveryClient();
        var service = new PartyDiscoverySearchService(drtMock, consumerMock, NullLogger<PartyDiscoverySearchService>.Instance);

        var result = await service.DiscoverDrtCandidatesAsync(
            companyNames: ["State Bank of India"],
            tribunalIds: [1]);

        Assert.Equal(2, result.TotalCandidatesFound);
        Assert.Equal(1, result.AcceptedCandidatesCount);
        Assert.Equal(1, result.RejectedCandidatesCount);

        var accepted = Assert.Single(result.Candidates, c => c.IsAccepted);
        Assert.Equal("OA/100/2020", accepted.CaseNumber);
        Assert.Equal(PartyMatchOutcome.Verified, accepted.MatchResult.Outcome);

        var rejected = Assert.Single(result.Candidates, c => !c.IsAccepted);
        Assert.Equal("OA/101/2020", rejected.CaseNumber);
        Assert.Equal(PartyMatchOutcome.Rejected, rejected.MatchResult.Outcome);
        Assert.Equal("extra-words", rejected.MatchResult.Rule);
    }

    [Fact]
    public async Task DiscoverConsumerCandidatesAsync_recognizes_former_names_and_rejects_unrelated_companies()
    {
        var consumerMock = new MockConsumerDiscoveryClient();

        // 1. Former name match
        consumerMock.AddCase(1, 11000000, new ConsumerCompanyCase(
            CaseNumber: "CC/50/2019",
            FileApplicationNumber: null,
            CaseStageName: "DISPOSED",
            CaseFilingDate: new DateOnly(2019, 3, 1),
            DateOfHearing: null,
            DateOfNextHearing: null,
            DateOfDisposal: new DateOnly(2021, 5, 10),
            HearingSequenceNumber: null,
            ComplainantName: "RAMESH KUMAR",
            ComplainantAdvocateName: null,
            RespondentName: "OLD HERITAGE BANK LIMITED",
            RespondentAdvocateName: null,
            CaseTypeName: "CC",
            CaseCategoryName: null,
            CommissionName: "NCDRC",
            CommissionId: 11000000,
            CommissionTypeId: 1,
            StateName: "Delhi",
            DistrictName: null,
            EffectiveHearingDate: null,
            NormalizedCaseNumber: CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: "CC/50/2019"))));

        // 2. Extra words group company negative test (contains registered name + extra words)
        consumerMock.AddCase(1, 11000000, new ConsumerCompanyCase(
            CaseNumber: "CC/51/2019",
            FileApplicationNumber: null,
            CaseStageName: "PENDING",
            CaseFilingDate: new DateOnly(2019, 4, 1),
            DateOfHearing: null,
            DateOfNextHearing: null,
            DateOfDisposal: null,
            HearingSequenceNumber: null,
            ComplainantName: "SURESH SHARMA",
            ComplainantAdvocateName: null,
            RespondentName: "HERITAGE BANK LIMITED SECURITIES",
            RespondentAdvocateName: null,
            CaseTypeName: "CC",
            CaseCategoryName: null,
            CommissionName: "NCDRC",
            CommissionId: 11000000,
            CommissionTypeId: 1,
            StateName: "Delhi",
            DistrictName: null,
            EffectiveHearingDate: null,
            NormalizedCaseNumber: CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: "CC/51/2019"))));

        var drtMock = new MockDrtDiscoveryClient();
        var service = new PartyDiscoverySearchService(drtMock, consumerMock, NullLogger<PartyDiscoverySearchService>.Instance);

        var result = await service.DiscoverConsumerCandidatesAsync(
            companyNames: ["Heritage Bank Limited"],
            formerNames: ["Old Heritage Bank Limited"],
            commissions: [(1, 11000000)]);

        Assert.Equal(2, result.TotalCandidatesFound);
        Assert.Equal(1, result.AcceptedCandidatesCount);
        Assert.Equal(1, result.RejectedCandidatesCount);

        var accepted = Assert.Single(result.Candidates, c => c.IsAccepted);
        Assert.Equal("CC/50/2019", accepted.CaseNumber);
        Assert.Equal(PartyMatchOutcome.Verified, accepted.MatchResult.Outcome);
        Assert.Equal("exact-former-name", accepted.MatchResult.Rule);

        var rejected = Assert.Single(result.Candidates, c => !c.IsAccepted);
        Assert.Equal("CC/51/2019", rejected.CaseNumber);
        Assert.Equal(PartyMatchOutcome.Rejected, rejected.MatchResult.Outcome);
        Assert.Equal("extra-words", rejected.MatchResult.Rule);
    }

    [Fact]
    public async Task DiscoverDrtCandidatesAsync_deduplicates_same_case_surfaced_under_multiple_queries()
    {
        var drtMock = new MockDrtDiscoveryClient();

        var sharedCase = new DrtPartySearchResult(
            TribunalId: 1,
            RawCaseType: "OA",
            RawCaseNo: "OA/500/2021",
            CaseNumber: CaseNumberNormaliser.Parse(new CaseNumberInput(CaseNumber: "OA/500/2021")),
            DiaryNo: "500/2021",
            DateOfFiling: new DateOnly(2021, 1, 1),
            Applicant: "KOTAK MAHINDRA BANK LIMITED",
            Respondent: "BORROWER INC",
            PetitionerAdvocate: null,
            RespondentAdvocate: null,
            FilingNo: null);

        drtMock.AddResult(1, sharedCase);

        var consumerMock = new MockConsumerDiscoveryClient();
        var service = new PartyDiscoverySearchService(drtMock, consumerMock, NullLogger<PartyDiscoverySearchService>.Instance);

        // Searching with both canonical name and abbreviation variant
        var result = await service.DiscoverDrtCandidatesAsync(
            companyNames: ["Kotak Mahindra Bank Limited", "Kotak Mahindra Bank Ltd"],
            tribunalIds: [1]);

        // Same case should be collapsed in the output candidate list
        Assert.Equal(1, result.TotalCandidatesFound);
        Assert.Equal(1, result.AcceptedCandidatesCount);
        var cand = Assert.Single(result.Candidates);
        Assert.Equal("OA/500/2021", cand.CaseNumber);
        Assert.True(cand.IsAccepted);
    }

    private sealed class MockDrtDiscoveryClient : IDrtClient
    {
        public List<string> Queries { get; } = [];
        private readonly Dictionary<int, List<DrtPartySearchResult>> _results = [];

        public string CurrentExit => "127.0.0.1:8080";

        public void AddResult(int tribunalId, DrtPartySearchResult result)
        {
            if (!_results.TryGetValue(tribunalId, out var list))
            {
                list = [];
                _results[tribunalId] = list;
            }
            list.Add(result);
        }

        public Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtTribunal>>([new(1, "DRT 1 Delhi", false, "Delhi", "Delhi", "DRT 1")]);

        public Task<IReadOnlyList<DrtCaseType>> GetCaseTypesAsync(int tribunalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtCaseType>>([]);

        public Task<IReadOnlyList<DrtPartySearchResult>> SearchByPartyNameAsync(int tribunalId, string partyName, CancellationToken ct = default)
        {
            Queries.Add(partyName);
            return Task.FromResult<IReadOnlyList<DrtPartySearchResult>>(_results.GetValueOrDefault(tribunalId) ?? []);
        }

        public Task<DrtCaseLookupResult> GetCaseByNumberAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtOrder>>([]);

        public Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtOrder>>([]);

        public Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20971520, CancellationToken ct = default) =>
            Task.FromResult(DrtOrderDownloadResult.NotAvailable());
    }

    private sealed class MockConsumerDiscoveryClient : IConsumerClient
    {
        public List<string> Queries { get; } = [];
        private readonly Dictionary<(int, int), List<ConsumerCompanyCase>> _cases = [];

        public string CurrentExit => "127.0.0.1:8080";
        public ConsumerProxyRotator Rotator => new([]);

        public void AddCase(int typeId, int commId, ConsumerCompanyCase c)
        {
            var key = (typeId, commId);
            if (!_cases.TryGetValue(key, out var list))
            {
                list = [];
                _cases[key] = list;
            }
            list.Add(c);
        }

        public Task<ConsumerCaseLookupResult> GetCaseStatusAsync(string caseNumber, CancellationToken ct = default) =>
            Task.FromResult(ConsumerCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<ConsumerCompanySearchResult> SearchByCompanyAsync(int commissionTypeId, int commissionId, string keyword, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default)
        {
            Queries.Add(keyword);
            return Task.FromResult(new ConsumerCompanySearchResult(_cases.GetValueOrDefault((commissionTypeId, commissionId)) ?? [], true));
        }

        public Task<ConsumerOrderDownloadResult> GetDailyOrderPdfAsync(long filingReferenceNumber, DateOnly dateOfHearing, int orderTypeId = 1, CancellationToken ct = default) =>
            Task.FromResult(ConsumerOrderDownloadResult.NotFound());
    }
}

