using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>EntityCrossReferenceRules.Evaluate appends outcomes in a fixed order: [0] charge holder vs
/// litigants, [1] director/guarantor vs litigants, [2] charge holder vs requesting client. Built from three
/// real, verified examples on Coastal Projects Limited (request 4): State Bank of India is both a charge
/// holder and an NCLT petitioner; director SURENDRA BABU SABBINENI is a personal guarantor and a litigant
/// across several writ petitions; HDFC Bank is both the requesting client and a charge holder. Every
/// Litigation fixture below sets CaseStatus="Pending" explicitly — it defaults to null, and
/// LitigationRules.IsPending(null) is false, so an omitted CaseStatus would silently never match.</summary>
public class EntityCrossReferenceRulesTests
{
    // ── Check 1: charge holder ↔ litigant ──────────────────────────────────────────────────────────

    [Fact]
    public void ChargeHolder_MatchingLitigant_TriggersReview()
    {
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "State Bank of India" };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "TP 255/2019", CaseStatus = "Pending", Litigants = "State Bank of India vs. Test Co" };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, result[0].Status);
        Assert.Equal(FindingSeverity.Review, result[0].Finding!.Severity);
        Assert.Contains("\"entityType\":\"Litigation\"", result[0].Finding!.SourceReferenceJson);
        Assert.Contains("10", result[0].Finding!.SourceReferenceJson);
    }

    [Fact]
    public void ChargeHolder_CorporateSuffixVariance_StillMatches()
    {
        // Same lender, two different raw strings — proves the local suffix-stripping normalization (not
        // just NameNormalizer.Normalize, which does no suffix stripping) is doing the work.
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "Aditya Birla Finance Limited" };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "ADITYA BIRLA FINANCE LTD. vs. Test Co" };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, result[0].Status);
    }

    [Fact]
    public void ChargeHolder_NoOverlap_NotTriggered()
    {
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "HDFC Bank Limited" };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "Some unrelated party vs. Test Co" };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
    }

    [Fact]
    public void ChargeHolder_SatisfiedCharge_NotTriggered()
    {
        // Review finding #196: a repaid/satisfied charge is historical exposure, not a current holding —
        // must never describe a former lender as "an existing charge holder."
        var charge = new RocCharge
        {
            ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "State Bank of India",
            SatisfactionDate = new DateOnly(2020, 1, 1)
        };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "TP 255/2019", CaseStatus = "Pending", Litigants = "State Bank of India vs. Test Co" };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
    }

    [Fact]
    public void ChargeHolder_UnconfirmedLitigationMatch_NotTriggered()
    {
        // Review finding #196: LegalHistoryParser marks a case Probable/Uncertain when the data vendor
        // hasn't confirmed it belongs to this company at all — LitigationRules itself never treats such a
        // row as a confirmed adverse signal, and neither should this rule.
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "State Bank of India" };
        var lit = new Litigation
        {
            LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "State Bank of India vs. Test Co",
            MatchStatus = LitigationMatchStatus.Probable
        };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
    }

    [Fact]
    public void ChargeHolder_ResolvedLitigation_NotTriggered()
    {
        // Review finding #196: a disposed/closed/dismissed case is resolved, historical exposure — the
        // same "closed" definition LitigationRules.IsPending already uses to exclude cases from its own
        // findings, reused rather than duplicated.
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "State Bank of India" };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Disposed", Litigants = "State Bank of India vs. Test Co" };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
    }

    // ── Check 2: director/guarantor ↔ litigant ─────────────────────────────────────────────────────

    [Fact]
    public void Director_MatchingLitigant_TriggersWatch()
    {
        var director = new Director { DirectorId = 1, NameRaw = "SURENDRA BABU SABBINENI" };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "WP/15439/2020", CaseStatus = "Pending", Litigants = "Sabbineni Surendra vs. Test Co" };
        var ctx = BuildContext(directors: [director], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, result[1].Status);
        Assert.Equal(FindingSeverity.Watch, result[1].Finding!.Severity);
        Assert.Contains("\"entityType\":\"Litigation\"", result[1].Finding!.SourceReferenceJson);
    }

    [Fact]
    public void PersonalGuarantor_ExtractedFromChargeParticulars_MatchesLitigant()
    {
        // The director's own full name won't substring-match "G Hari Hara Rao" (different person, and
        // even for the same guarantor a bare initial like "S.Surendra" doesn't contain "Sabbineni") — this
        // proves the guarantor-extraction path, not just the director-name path, feeds real matches.
        var charge = new RocCharge
        {
            ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "Bank",
            Events = [new RocChargeEvent
            {
                EventType = ChargeEventType.Creation,
                PropertyParticulars = "Personal Guarantee of Shri. S. Surendra and Shri. G. Hari Hara Rao."
            }]
        };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "G Hari Hara Rao vs. Test Co" };
        var ctx = BuildContext(charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, result[1].Status);
    }

    [Fact]
    public void TrailingDesignationAfterComma_NeverExtractedAsASecondGuarantor()
    {
        // Real bug found against live Coastal data: "Personal guarantee of Mr. S. Surendra, Managing
        // Director" was extracting "Managing Director" as if it were a second guarantor's own name — it's
        // a trailing designation describing the person just named, not another person. An unrelated litigant
        // mentioning only the generic designation must not falsely trigger.
        var director = new Director { DirectorId = 1, NameRaw = "Rajesh Prakash Deshmukh" };
        var charge = new RocCharge
        {
            ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "Bank",
            Events = [new RocChargeEvent
            {
                EventType = ChargeEventType.Creation,
                PropertyParticulars = "Personal guarantee of Mr. S. Surendra, Managing Director"
            }]
        };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "XYZ Company, Managing Director vs. Test Co" };
        var ctx = BuildContext(directors: [director], charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void CorporateGuarantee_NeverExtractedAsPersonalGuarantor()
    {
        // Real false-positive shape: must require the literal word "Personal" before "Guarantee". An
        // unrelated director is included so the check actually evaluates (rather than short-circuiting to
        // NotEvaluated for having zero candidate names at all) — proving specifically that the corporate
        // guarantee text contributes no spurious match, not just that nothing happens to trigger.
        var director = new Director { DirectorId = 1, NameRaw = "Rajesh Prakash Deshmukh" };
        var charge = new RocCharge
        {
            ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "Bank",
            Events = [new RocChargeEvent
            {
                EventType = ChargeEventType.Creation,
                PropertyParticulars = "The Company has given Corporate Guarantee for a Loan Amount of Rs. 76.00 Crores."
            }]
        };
        // Litigants deliberately shares no token with the director's name, so the only way this could
        // trigger is a stray guarantor extraction from the corporate-guarantee text above.
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "Orissa State Financial Corporation vs. Test Co" };
        var ctx = BuildContext(directors: [director], charges: [charge], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void ShortCommonSurname_AgainstUnrelatedDirector_NotTriggered()
    {
        // Precision-over-recall guard: matching is on the director's single longest/most-distinctive name
        // token, not any shared token, so an unrelated litigant mentioning only a common surname doesn't
        // falsely implicate a director whose name happens to share it.
        var director = new Director { DirectorId = 1, NameRaw = "Ajay Dilkush Sarupria" }; // longest token: SARUPRIA
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Pending", Litigants = "Ramesh Kumar vs. Test Co" };
        var ctx = BuildContext(directors: [director], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void Director_ResolvedLitigation_NotTriggered()
    {
        // Same pending/resolved definition applies to Check 2 as Check 1.
        var director = new Director { DirectorId = 1, NameRaw = "SURENDRA BABU SABBINENI" };
        var lit = new Litigation { LitigationId = 10, CaseNumber = "1", CaseStatus = "Dismissed", Litigants = "Sabbineni Surendra vs. Test Co" };
        var ctx = BuildContext(directors: [director], litigations: [lit]);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    // ── Check 3: charge holder ↔ requesting client ─────────────────────────────────────────────────

    [Fact]
    public void ChargeHolder_MatchesRequestingClient_TriggersWatch()
    {
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "HDFC Bank Limited" };
        var client = new Client { ClientId = 1, ClientName = "HDFC Bank" };
        var ctx = BuildContext(charges: [charge], client: client);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, result[2].Status);
        Assert.Equal(FindingSeverity.Watch, result[2].Finding!.Severity);
        Assert.Contains("\"entityType\":\"RocCharge\"", result[2].Finding!.SourceReferenceJson);
        Assert.Contains("1", result[2].Finding!.SourceReferenceJson);
    }

    [Fact]
    public void ChargeHolder_NoClientOnFile_IsNotEvaluated()
    {
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "HDFC Bank Limited" };
        var ctx = BuildContext(charges: [charge]); // no client passed

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, result[2].Status);
    }

    [Fact]
    public void ChargeHolder_DifferentFromClient_NotTriggered()
    {
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "Kotak Mahindra Bank Limited" };
        var client = new Client { ClientId = 1, ClientName = "HDFC Bank" };
        var ctx = BuildContext(charges: [charge], client: client);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[2].Status);
    }

    [Fact]
    public void ChargeHolder_SatisfiedChargeVsClient_NotTriggered()
    {
        // Review finding #196: a satisfied charge must not be claimed as something the client "already
        // holds" in the present tense.
        var charge = new RocCharge
        {
            ChargeId = 1, RocChargeNumber = "C1", LatestChargeHolderRaw = "HDFC Bank Limited",
            SatisfactionDate = new DateOnly(2019, 1, 1)
        };
        var client = new Client { ClientId = 1, ClientName = "HDFC Bank" };
        var ctx = BuildContext(charges: [charge], client: client);

        var result = EntityCrossReferenceRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[2].Status);
    }
}
