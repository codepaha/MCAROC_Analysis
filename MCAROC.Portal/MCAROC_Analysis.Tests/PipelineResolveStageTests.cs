using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #295: the Resolve stage and the trust ladder, as the pure decider sees them. An ambiguous,
/// unmatched, unconfirmed or refuted identity parks the request and holds back everything that would act on the
/// company; a low-confidence auto-selection may not start the litigation search on its own.</summary>
public sealed class PipelineResolveStageTests
{
    private const string Cin = "U45203OR1995PLC003982";
    private static readonly PipelinePolicy SearchOn = new() { LitigationSearch = true };

    private static IdentityFacts Facts(ResolutionStatus status, string code, ResolutionMethod? method = null, bool applied = false,
        string? chosen = null, string? recommended = null, double? score = null, long? existing = null, long id = 70) =>
        new(id, status, method, code, applied, chosen, recommended, score, existing);

    /// <summary>A name-only request: no identifier and no auto-fetch job yet.</summary>
    private static PipelineSnapshot NameOnly(IdentityFacts latest) => new()
    {
        RequestId = 1, RequestStatus = RequestStatus.Created, LatestResolution = latest, LitigationConfigured = true
    };

    /// <summary>An auto-fetch request ingested and analysed, ready for the litigation search.</summary>
    private static PipelineSnapshot Ingested(IdentityFacts? applied) => new()
    {
        RequestId = 1, Cin = Cin, RequestStatus = RequestStatus.AnalysisCompleted,
        AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Completed, RocDocumentId: 7, IncludeFilings: false, null, null),
        LatestCompletedIngestionRunId = 10, LatestIngestionStatus = IngestionRunStatus.CompletedClean,
        Analysis = new(20, AnalysisRunStatus.Completed, null), CalcAssuranceMode = CalculationAssuranceMode.Off,
        LitigationConfigured = true, LatestResolution = applied, AppliedResolution = applied
    };

    private static readonly PipelineStage[] Gated = [PipelineStage.Unlock, PipelineStage.Refresh, PipelineStage.Fetch, PipelineStage.Ingest, PipelineStage.Litigation];

    [Theory]
    [InlineData(ResolutionStatus.Ambiguous, "IDENTITY_AMBIGUOUS", "IDENTITY_AMBIGUOUS")]
    [InlineData(ResolutionStatus.NotFound, "IDENTITY_NOT_FOUND", "IDENTITY_NOT_FOUND")]
    [InlineData(ResolutionStatus.InvalidInput, "IDENTIFIER_NOT_IN_MASTER", "IDENTITY_NOT_FOUND")]
    [InlineData(ResolutionStatus.NeedsConfirmation, "AUTO_SELECT_DISABLED", "IDENTITY_NEEDS_CONFIRMATION")]
    public void An_unresolved_identity_needs_attention_and_nothing_downstream_can_run(ResolutionStatus status, string recorded, string expected)
    {
        var d = PipelineDecider.Decide(NameOnly(Facts(status, recorded, recommended: status == ResolutionStatus.NeedsConfirmation ? Cin : null)), SearchOn);

        var resolve = d.Stages[PipelineStage.Resolve];
        Assert.Equal(PipelineStageStateKind.NeedsAttention, resolve.State);
        Assert.Equal(expected, resolve.ReasonCode);
        Assert.Equal(70, resolve.SourceRef); // the resolution row the ambiguity queue shows
        foreach (var stage in Gated)
        {
            Assert.Equal(PipelineStageStateKind.Waiting, d.Stages[stage].State);
            Assert.Equal(PipelineDecider.AwaitingResolve, d.Stages[stage].ReasonCode);
        }
        Assert.Equal(PipelineOutcome.NeedsAttention, d.Outcome);
        Assert.False(d.CoreReady);
    }

    [Fact]
    public void Needs_confirmation_names_the_recommended_company_and_invalid_input_keeps_its_own_code()
    {
        var confirm = PipelineDecider.Decide(NameOnly(Facts(ResolutionStatus.NeedsConfirmation, "IDENTITY_NEEDS_CONFIRMATION", recommended: Cin)), SearchOn);
        Assert.Contains(Cin, confirm.Stages[PipelineStage.Resolve].ReasonDetail);

        var invalid = PipelineDecider.Decide(NameOnly(Facts(ResolutionStatus.InvalidInput, "IDENTIFIER_INVALID")), SearchOn);
        Assert.StartsWith("IDENTIFIER_INVALID", invalid.Stages[PipelineStage.Resolve].ReasonDetail);
    }

    [Fact]
    public void A_selection_that_collided_with_the_clients_existing_request_points_at_it()
    {
        var latest = Facts(ResolutionStatus.Resolved, ResolutionReasonCodes.DuplicateRequest, ResolutionMethod.HumanSelected, chosen: Cin, existing: 44);
        var d = PipelineDecider.Decide(NameOnly(latest), SearchOn);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Resolve].State);
        Assert.Equal("DUPLICATE_REQUEST", d.Stages[PipelineStage.Resolve].ReasonCode);
        Assert.Contains("44", d.Stages[PipelineStage.Resolve].ReasonDetail);
    }

    [Fact]
    public void A_false_accept_reopens_resolve_and_the_failed_fetch_waits_on_it_instead_of_raising_its_own_attention()
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = null, RequestStatus = RequestStatus.ExtractionFailed,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Failed, null, false, null, "IDENTITY_FALSE_ACCEPT: ..."),
            LatestResolution = Facts(ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FalseAccept, id: 71),
            AppliedResolution = Facts(ResolutionStatus.Resolved, ResolutionReasonCodes.AutoSelected, ResolutionMethod.AutoSelected, true, Cin, score: 0.97)
        };
        var d = PipelineDecider.Decide(s, SearchOn);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Resolve].State);
        Assert.Equal("IDENTITY_FALSE_ACCEPT", d.Stages[PipelineStage.Resolve].ReasonCode);
        Assert.Equal(71, d.Stages[PipelineStage.Resolve].SourceRef);
        Assert.Equal(PipelineDecider.AwaitingResolve, d.Stages[PipelineStage.Fetch].ReasonCode);
        Assert.Equal(PipelineDecider.AwaitingResolve, d.Stages[PipelineStage.Ingest].ReasonCode);
        // Only one stage asks for a person.
        Assert.Single(d.Stages, kv => kv.Value.State == PipelineStageStateKind.NeedsAttention);
    }

    [Fact]
    public void A_false_accept_wins_even_if_the_request_still_carries_the_refuted_identifier()
    {
        var s = Ingested(null) with { LatestResolution = Facts(ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FalseAccept) };
        Assert.Equal("IDENTITY_FALSE_ACCEPT", PipelineDecider.Decide(s, SearchOn).Stages[PipelineStage.Resolve].ReasonCode);
    }

    [Fact]
    public void A_person_selecting_the_company_unblocks_the_stage()
    {
        var human = Facts(ResolutionStatus.Resolved, ResolutionReasonCodes.HumanSelected, ResolutionMethod.HumanSelected, true, Cin, id: 72);
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = Cin, RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Queued, null, false, null, null),
            LatestResolution = human, AppliedResolution = human
        };
        var d = PipelineDecider.Decide(s, SearchOn);

        Assert.Equal(PipelineStageStateKind.Succeeded, d.Stages[PipelineStage.Resolve].State);
        Assert.Equal("RESOLVED_HUMAN_SELECTED", d.Stages[PipelineStage.Resolve].ReasonCode);
        Assert.Equal(72, d.Stages[PipelineStage.Resolve].SourceRef);
        Assert.Equal("QUEUED", d.Stages[PipelineStage.Fetch].ReasonCode);
    }

    [Theory]
    [InlineData(ResolutionReasonCodes.RequestAlreadyIdentified, null)]
    [InlineData(ResolutionReasonCodes.DuplicateRequest, 44L)]
    public void A_refused_selection_of_another_company_disputes_the_existing_identifier_and_holds_everything_back(string code, long? existing)
    {
        const string other = "U12345KA2022PTC654321";
        var refused = Facts(ResolutionStatus.Resolved, code, ResolutionMethod.HumanSelected, applied: false, chosen: other, existing: existing, id: 73);
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = Cin, RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Queued, null, false, null, null),
            LatestResolution = refused, LitigationConfigured = true
        };
        var d = PipelineDecider.Decide(s, SearchOn);

        var resolve = d.Stages[PipelineStage.Resolve];
        Assert.Equal(PipelineStageStateKind.NeedsAttention, resolve.State);
        Assert.Equal(code, resolve.ReasonCode);
        Assert.Equal(73, resolve.SourceRef);
        Assert.Contains(other, resolve.ReasonDetail);
        Assert.Contains(Cin, resolve.ReasonDetail);
        foreach (var stage in Gated)
            Assert.Equal(PipelineDecider.AwaitingResolve, d.Stages[stage].ReasonCode);
        Assert.Equal(PipelineOutcome.NeedsAttention, d.Outcome);
    }

    [Fact]
    public void Confirming_the_existing_company_after_a_refused_selection_unblocks_the_stage()
    {
        var confirmed = Facts(ResolutionStatus.Resolved, ResolutionReasonCodes.HumanSelected, ResolutionMethod.HumanSelected, true, Cin, id: 74);
        var s = Ingested(confirmed);
        var d = PipelineDecider.Decide(s, SearchOn);
        Assert.Equal(PipelineStageStateKind.Succeeded, d.Stages[PipelineStage.Resolve].State);

        // A refused re-selection of the same company the request already names is not a dispute.
        var same = Facts(ResolutionStatus.Resolved, ResolutionReasonCodes.DuplicateRequest, ResolutionMethod.HumanSelected, false, Cin, existing: 44);
        Assert.Equal(PipelineStageStateKind.Succeeded,
            PipelineDecider.Decide(s with { LatestResolution = same }, SearchOn).Stages[PipelineStage.Resolve].State);
    }

    [Theory]
    [InlineData(ResolutionMethod.HumanSelected, null, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.995, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.97, false)]
    public void The_litigation_analysis_starts_on_its_own_only_for_a_trusted_identity(ResolutionMethod method, double? score, bool autoStarts)
    {
        var applied = Facts(ResolutionStatus.Resolved, "RESOLVED", method, true, Cin, score: score);
        var s = Ingested(applied) with
        {
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.Completed)
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true, LitigationAnalysis = true });

        var analysis = d.Stages[PipelineStage.LitigationAnalysis];
        Assert.Equal(PipelineStageStateKind.NotStarted, analysis.State);
        Assert.Equal(autoStarts ? PipelineDecider.ReadyToStart : PipelineDecider.AwaitingTrustedIdentity, analysis.ReasonCode);
    }

    [Fact]
    public void A_request_identified_at_intake_resolves_as_before()
    {
        var d = PipelineDecider.Decide(Ingested(null), SearchOn);

        Assert.Equal(PipelineStageStateKind.Succeeded, d.Stages[PipelineStage.Resolve].State);
        Assert.Null(d.Stages[PipelineStage.Resolve].ReasonCode);
        Assert.Equal(PipelineDecider.ReadyToStart, d.Stages[PipelineStage.Litigation].ReasonCode);
    }

    [Theory]
    [InlineData(ResolutionMethod.UserProvidedCin, null, true)]
    [InlineData(ResolutionMethod.HumanSelected, null, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.995, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.99, true)]
    [InlineData(ResolutionMethod.AutoSelected, 0.97, false)]
    public void The_litigation_search_starts_on_its_own_only_for_a_trusted_identity(ResolutionMethod method, double? score, bool autoStarts)
    {
        var applied = Facts(ResolutionStatus.Resolved, "RESOLVED", method, true, Cin, score: score);
        var d = PipelineDecider.Decide(Ingested(applied), SearchOn);

        var litigation = d.Stages[PipelineStage.Litigation];
        Assert.Equal(PipelineStageStateKind.NotStarted, litigation.State); // free steps and a reviewer's start stay open either way
        Assert.Equal(autoStarts ? PipelineDecider.ReadyToStart : PipelineDecider.AwaitingTrustedIdentity, litigation.ReasonCode);
        // Everything else is unaffected: the request still completes its core track.
        Assert.True(d.CoreReady);
    }

    [Fact]
    public void The_spend_threshold_comes_from_the_snapshot()
    {
        var applied = Facts(ResolutionStatus.Resolved, "RESOLVED_AUTO_SELECTED", ResolutionMethod.AutoSelected, true, Cin, score: 0.97);
        var d = PipelineDecider.Decide(Ingested(applied) with { SpendThreshold = 0.96 }, SearchOn);
        Assert.Equal(PipelineDecider.ReadyToStart, d.Stages[PipelineStage.Litigation].ReasonCode);
    }

    [Fact]
    public void A_search_already_running_is_still_reported_while_the_identity_is_reopened()
    {
        var s = Ingested(null) with
        {
            Cin = null,
            LatestResolution = Facts(ResolutionStatus.NeedsConfirmation, ResolutionReasonCodes.FalseAccept),
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Polling, null, null, null)
        };
        Assert.Equal(PipelineStageStateKind.Running, PipelineDecider.Decide(s, SearchOn).Stages[PipelineStage.Litigation].State);
    }

    [Fact]
    public void Generated_while_the_identity_is_unresolved_nothing_is_ever_ready_to_start_and_no_company_stage_is_done()
    {
        var rng = new Random(295);
        for (var i = 0; i < 2000; i++)
        {
            var status = Enum.GetValues<ResolutionStatus>()[rng.Next(5)];
            var latest = rng.Next(4) == 0 ? null : Facts(status, rng.Next(3) == 0 ? ResolutionReasonCodes.FalseAccept : "X",
                status == ResolutionStatus.Resolved ? ResolutionMethod.AutoSelected : null, rng.Next(2) == 0,
                rng.Next(3) == 0 ? "U12345KA2022PTC654321" : Cin, score: rng.NextDouble());
            var s = new PipelineSnapshot
            {
                RequestId = 1,
                Cin = rng.Next(2) == 0 ? null : Cin,
                RequestStatus = RequestStatus.Created,
                AutoFetch = rng.Next(2) == 0 ? null : new AutoFetchFacts(5, Enum.GetValues<AutoFetchJobStatus>()[rng.Next(Enum.GetValues<AutoFetchJobStatus>().Length)],
                    null, false, null, null),
                LatestResolution = latest,
                AppliedResolution = latest is { AppliedToRequest: true } ? latest : null,
                LitigationConfigured = true,
                LatestCompletedIngestionRunId = rng.Next(3) == 0 ? 10 : null,
                LitigationSearch = rng.Next(3) == 0
                    ? new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.Completed)
                    : null
            };
            var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true, LitigationAnalysis = true });

            var resolve = d.Stages[PipelineStage.Resolve];
            if (resolve.IsDone || resolve.State == PipelineStageStateKind.Skipped) continue;
            Assert.DoesNotContain(d.Stages.Values, v => v.ReasonCode == PipelineDecider.ReadyToStart);
            foreach (var stage in new[] { PipelineStage.Unlock, PipelineStage.Refresh, PipelineStage.Fetch })
                Assert.False(d.Stages[stage].IsDone, $"trial {i}: {stage} done while Resolve is {resolve.State}");
            if (s.LatestCompletedIngestionRunId is null)
                Assert.False(d.Stages[PipelineStage.Ingest].IsDone, $"trial {i}: Ingest done while Resolve is {resolve.State}");
        }
    }
}
