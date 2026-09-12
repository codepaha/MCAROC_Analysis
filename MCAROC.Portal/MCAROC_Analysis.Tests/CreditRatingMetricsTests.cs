using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CreditRatingMetricsTests
{
    private static DossierModel CreateModelWithRatings(
        List<CreditRating> ratings,
        SheetCoverage? sourceCoverage = null,
        List<RocCharge>? openCharges = null)
    {
        var charges = openCharges ?? [];
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", DateTime.UtcNow, DateTime.UtcNow),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges(charges, charges, [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], [], ratings),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: sourceCoverage ?? SheetCoverage.Empty,
            Metrics: []);
    }

    private static MetricResult M(MetricGroup group, string label) =>
        Assert.Single(group.Metrics, m => m.Label == label);

    // ─────────────────────────────────────────────────────────────────────────
    // Empty / Absent Sheet Coverage
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_ratings_with_sheets_absent_emits_insufficient_with_upload_reason()
    {
        var run = new IngestionRun
        {
            AbsentOptionalSheetsJson = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                SheetAliases.CanonicalName(SheetAliases.CreditRatings),
                SheetAliases.CanonicalName(SheetAliases.UnacceptedRatings)
            })
        };
        var coverage = SheetCoverage.From(run);

        var model = CreateModelWithRatings([], coverage);
        var group = DossierComputations.CreditRatingMetrics(model);

        Assert.Equal("Credit ratings", group.Title);
        Assert.All(group.Metrics, m =>
        {
            Assert.False(m.HasValue);
            Assert.Equal("Credit Ratings and Unaccepted Ratings sheets not in this upload", m.DisplayValue());
        });
    }

    [Fact]
    public void Empty_ratings_with_sheets_present_emits_insufficient_with_file_reason()
    {
        var model = CreateModelWithRatings([]);
        var group = DossierComputations.CreditRatingMetrics(model);

        Assert.Equal("Credit ratings", group.Title);
        Assert.All(group.Metrics, m =>
        {
            Assert.False(m.HasValue);
            Assert.Equal("No credit rating records on file", m.DisplayValue());
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // F3 / F4 Scale Gate Regressions
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F3_and_F4_are_blocked_by_scale_gate_and_never_compute_unscaled_crore_arithmetic()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Amount = 50000000m,
                Currency = "INR",
                Rating = "CRISIL AA",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f3 = M(group, "Total rated amount");
        Assert.False(f3.HasValue);
        Assert.Null(f3.Value);
        Assert.Contains("Source AMOUNT lacks explicit scale/denomination metadata", f3.InsufficiencyReason);
        Assert.Contains("CreditRating.Amount", f3.Inputs);

        var f4 = M(group, "Rated amount vs open charge coverage");
        Assert.False(f4.HasValue);
        Assert.Null(f4.Value);
        Assert.Contains("Blocked on total rated amount scale gate", f4.InsufficiencyReason);
        Assert.Contains("CreditRating.Amount", f4.Inputs);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // F1: Latest rating per instrument & Provenance
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F1_emits_ok_with_text_value_for_latest_rating()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL A",
                RatingDate = new DateOnly(2022, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL AA+",
                RatingDate = new DateOnly(2023, 6, 1),
                IsAccepted = true
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f1 = Assert.Single(group.Metrics, m => m.Label.StartsWith("Latest rating"));
        Assert.True(f1.HasValue);
        Assert.Null(f1.Value);
        Assert.Equal("CRISIL AA+", f1.TextValue);
        Assert.Equal("CRISIL AA+", f1.DisplayValue());
        Assert.Equal(MetricUnit.Text, f1.Unit);
        Assert.Equal("as of 1 Jun 2023", f1.Period);
    }

    [Fact]
    public void F1_preserves_concurrent_ratings_from_different_agencies()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL AA",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "ICRA",
                Instrument = "Term Loan",
                Rating = "[ICRA]AA+",
                RatingDate = new DateOnly(2023, 2, 1),
                IsAccepted = true
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var crisil = M(group, "Latest rating (Term Loan - CRISIL)");
        var icra = M(group, "Latest rating (Term Loan - ICRA)");

        Assert.True(crisil.HasValue);
        Assert.Equal("CRISIL AA", crisil.TextValue);

        Assert.True(icra.HasValue);
        Assert.Equal("[ICRA]AA+", icra.TextValue);
    }

    [Fact]
    public void F1_missing_agency_or_instrument_fails_closed_with_provenance()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "  ",
                Instrument = "Term Loan",
                Rating = "A",
                RatingDate = new DateOnly(2023, 1, 1),
                SourceSheetName = "Credit Ratings",
                SourceRowNumber = 5,
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "  ",
                Rating = "AA",
                RatingDate = new DateOnly(2023, 1, 1),
                SourceSheetName = "Credit Ratings",
                SourceRowNumber = 6,
                IsAccepted = true
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var missingAgency = Assert.Single(group.Metrics, m => m.Label == "Latest rating (Term Loan)");
        Assert.False(missingAgency.HasValue);
        Assert.Contains("missing Agency", missingAgency.InsufficiencyReason);
        Assert.Contains("sheet 'Credit Ratings', row 5", missingAgency.InsufficiencyReason);
        Assert.Contains("CreditRating.SourceSheetName", missingAgency.Inputs);
        Assert.Contains("CreditRating.SourceRowNumber", missingAgency.Inputs);

        var missingInst = Assert.Single(group.Metrics, m => m.Label == "Latest rating (unspecified instrument)");
        Assert.False(missingInst.HasValue);
        Assert.Contains("missing Instrument name", missingInst.InsufficiencyReason);
        Assert.Contains("sheet 'Credit Ratings', row 6", missingInst.InsufficiencyReason);
    }

    [Fact]
    public void F1_missing_rating_date_fails_closed()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Working Capital",
                Rating = "CRISIL A1",
                RatingDate = null,
                SourceSheetName = "Credit Ratings",
                SourceRowNumber = 12,
                IsAccepted = true
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f1 = M(group, "Latest rating (Working Capital - CRISIL)");
        Assert.False(f1.HasValue);
        Assert.Contains("missing RatingDate", f1.InsufficiencyReason);
        Assert.Contains("sheet 'Credit Ratings', row 12", f1.InsufficiencyReason);
    }

    [Fact]
    public void F1_rating_symbol_normalization_recognizes_identical_ratings_and_flags_conflicts()
    {
        // 1. "AA+" vs " aa+ " on same date -> identical, no conflict
        var matchingRatings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "AA+",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = " aa+ ",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            }
        };

        var modelMatch = CreateModelWithRatings(matchingRatings);
        var groupMatch = DossierComputations.CreditRatingMetrics(modelMatch);
        var f1Match = M(groupMatch, "Latest rating (Term Loan - CRISIL)");
        Assert.True(f1Match.HasValue);
        Assert.Equal("AA+", f1Match.TextValue);

        // 2. "AA+" vs "AA-" on same date -> conflict!
        var conflictingRatings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "AA+",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "AA-",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            }
        };

        var modelConflict = CreateModelWithRatings(conflictingRatings);
        var groupConflict = DossierComputations.CreditRatingMetrics(modelConflict);
        var f1Conflict = M(groupConflict, "Latest rating (Term Loan - CRISIL)");
        Assert.False(f1Conflict.HasValue);
        Assert.Contains("Conflicting ratings on 1 Jan 2023", f1Conflict.InsufficiencyReason);
    }

    [Fact]
    public void F1_withdrawn_action_with_null_rating_displays_withdrawn()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Commercial Paper",
                Rating = null,
                Action = "Withdrawn",
                RatingDate = new DateOnly(2023, 5, 10),
                IsAccepted = true
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f1 = M(group, "Latest rating (Commercial Paper - CRISIL)");
        Assert.True(f1.HasValue);
        Assert.Equal("Withdrawn", f1.TextValue);
        Assert.Equal("Withdrawn", f1.DisplayValue());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // F2: Rating action summary & Missing Action Disclosure
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F2_buckets_canonical_actions_and_discloses_missing_action_count()
    {
        var ratings = new List<CreditRating>
        {
            new() { Agency = "CRISIL", Action = "Assigned", IsAccepted = true },
            new() { Agency = "CRISIL", Action = "ASSIGNED", IsAccepted = true },
            new() { Agency = "ICRA", Action = "Reaffirmed", IsAccepted = true },
            new() { Agency = "CARE", Action = null, IsAccepted = true }, // 1 missing action out of 4
            new() { Agency = "CARE", Action = null, IsAccepted = false } // unaccepted rows don't count toward accepted total
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var assigned = M(group, "Rating action summary (Assigned)");
        Assert.True(assigned.HasValue);
        Assert.Equal(2m, assigned.Value);
        Assert.Equal("2 of 3 classified actions (1 of 4 accepted rows missing action)", assigned.Period);

        var reaffirmed = M(group, "Rating action summary (Reaffirmed)");
        Assert.True(reaffirmed.HasValue);
        Assert.Equal(1m, reaffirmed.Value);
        Assert.Equal("1 of 3 classified actions (1 of 4 accepted rows missing action)", reaffirmed.Period);
    }

    [Fact]
    public void F2_all_accepted_actions_missing_emits_insufficient()
    {
        var ratings = new List<CreditRating>
        {
            new() { Agency = "CRISIL", Action = null, IsAccepted = true },
            new() { Agency = "ICRA", Action = "   ", IsAccepted = true }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f2 = M(group, "Rating action summary");
        Assert.False(f2.HasValue);
        Assert.Contains("No rating actions recorded on file", f2.InsufficiencyReason);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // F5: Date-Aware & Fail-Closed Regressions
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F5_historical_gap_prevention_matches_only_on_or_before_unaccepted_date()
    {
        // Unaccepted rating on 2023-01-01 was BBB.
        // Prior accepted rating on 2022-06-01 was BBB.
        // LATER accepted rating on 2023-06-01 was upgraded to A.
        // Comparing with 2022-06-01: BBB == BBB -> gap = 0.
        // Must NEVER match against 2023-06-01 to falsely manufacture a historical gap!
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "BBB",
                RatingDate = new DateOnly(2022, 6, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "A",
                RatingDate = new DateOnly(2023, 6, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "BBB",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = false
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f5 = M(group, "Accepted vs unaccepted rating gap");
        Assert.True(f5.HasValue);
        Assert.Equal(0m, f5.Value);
    }

    [Fact]
    public void F5_cross_agency_counterexample_does_not_mix_agencies_and_fails_closed()
    {
        // Unaccepted CARE rating on Term Loan. Accepted CRISIL rating on Term Loan.
        // They must NOT be compared across different agencies!
        // Because CARE has no accepted comparator, F5 must fail closed to Insufficient.
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL A",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CARE",
                Instrument = "Term Loan",
                Rating = "CARE BBB",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = false
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f5 = M(group, "Accepted vs unaccepted rating gap");
        Assert.False(f5.HasValue);
        Assert.Contains("no accepted comparator from the same agency on or before that date", f5.InsufficiencyReason);
        Assert.Contains("CARE", f5.InsufficiencyReason);
    }

    [Fact]
    public void F5_mixed_valid_and_invalid_unaccepted_batch_fails_closed()
    {
        // Two unaccepted ratings:
        // 1. Working Capital - CRISIL: has valid comparator
        // 2. Term Loan - CRISIL: accepted comparator is DATED AFTER unaccepted date (no as-of comparator)
        // Entire F5 metric must fail closed to Insufficient; no Ok result is emitted.
        var ratings = new List<CreditRating>
        {
            // Accepted for Working Capital on 2022-01-01
            new()
            {
                Agency = "CRISIL",
                Instrument = "Working Capital",
                Rating = "A1",
                RatingDate = new DateOnly(2022, 1, 1),
                IsAccepted = true
            },
            // Accepted for Term Loan on 2024-01-01 (LATER than unaccepted 2023-01-01)
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "AA",
                RatingDate = new DateOnly(2024, 1, 1),
                IsAccepted = true
            },
            // Unaccepted 1: valid comparator exists
            new()
            {
                Agency = "CRISIL",
                Instrument = "Working Capital",
                Rating = "A1",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = false
            },
            // Unaccepted 2: no comparator on or before 2023-01-01
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "AA",
                RatingDate = new DateOnly(2023, 1, 1),
                IsAccepted = false
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f5 = M(group, "Accepted vs unaccepted rating gap");
        Assert.False(f5.HasValue);
        Assert.Contains("no accepted comparator from the same agency on or before that date", f5.InsufficiencyReason);
        Assert.Contains("Term Loan", f5.InsufficiencyReason);
    }

    [Fact]
    public void F5_symbol_normalization_recognizes_identical_ratings_without_false_gap()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "AA+",
                RatingDate = new DateOnly(2022, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "  aa+  ",
                RatingDate = new DateOnly(2022, 6, 1),
                IsAccepted = false
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f5 = M(group, "Accepted vs unaccepted rating gap");
        Assert.True(f5.HasValue);
        Assert.Equal(0m, f5.Value);
    }

    [Fact]
    public void F5_detects_genuine_rating_gap()
    {
        var ratings = new List<CreditRating>
        {
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL AA",
                RatingDate = new DateOnly(2022, 1, 1),
                IsAccepted = true
            },
            new()
            {
                Agency = "CRISIL",
                Instrument = "Term Loan",
                Rating = "CRISIL A",
                RatingDate = new DateOnly(2022, 6, 1),
                IsAccepted = false
            }
        };

        var model = CreateModelWithRatings(ratings);
        var group = DossierComputations.CreditRatingMetrics(model);

        var f5 = M(group, "Accepted vs unaccepted rating gap");
        Assert.True(f5.HasValue);
        Assert.Equal(1m, f5.Value);
        Assert.Contains("unaccepted 'CRISIL A' vs accepted 'CRISIL AA'", f5.Period);
    }
}
