using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>What a model call cost: the price list, the recording of usage against the assignment it was made for, and the per-purpose and per-unit
/// reading of it.</summary>
public sealed class AiCostTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly AiPricingOptions Prices = new();

    private static AiUsageRecord Call(string purpose, string model, int input, int output, int thought = 0, long? requestId = null) =>
        new() { Purpose = purpose, Model = model, InputTokens = input, OutputTokens = output, ThoughtTokens = thought, RequestId = requestId };

    // ── the price list ────────────────────────────────────────────────────

    [Fact]
    public void A_generation_costs_input_at_the_input_rate_and_output_and_reasoning_at_the_output_rate()
    {
        // 1M in at $0.30 + (0.4M answer + 0.6M reasoning) at $2.50
        var cost = AiCostCalculator.CostUsd(Call("x", "gemini-2.5-flash", 1_000_000, 400_000, 600_000), Prices);
        Assert.Equal(0.30m + 2.50m, cost);
    }

    [Fact]
    public void An_embedding_costs_input_only()
    {
        var cost = AiCostCalculator.CostUsd(Call("x", "gemini-embedding-001", 2_000_000, 0), Prices);
        Assert.Equal(0.30m, cost);
    }

    [Fact]
    public void A_dated_or_preview_model_id_prices_as_its_longest_matching_family()
    {
        var lite = AiCostCalculator.RateFor("gemini-2.5-flash-lite-preview-09-2025", Prices);
        var flash = AiCostCalculator.RateFor("gemini-2.5-flash-002", Prices);
        Assert.Equal(0.10m, lite!.InputPerMillion);   // not the shorter "gemini-2.5-flash" entry
        Assert.Equal(0.30m, flash!.InputPerMillion);
    }

    [Fact]
    public void A_model_with_no_price_is_unpriced_not_free()
    {
        Assert.Null(AiCostCalculator.CostUsd(Call("x", "some-new-model", 1_000_000, 1_000_000), Prices));
    }

    // ── what each purpose scales with ─────────────────────────────────────

    [Theory]
    [InlineData(AiUsagePurposes.FilingExtraction, AiCostDriver.FilingDocument)]
    [InlineData(AiUsagePurposes.EmbeddingFilings, AiCostDriver.FilingDocument)]
    [InlineData(AiUsagePurposes.EmbeddingOrders, AiCostDriver.Order)]
    [InlineData(AiUsagePurposes.OrderClassification, AiCostDriver.Order)]
    [InlineData(AiUsagePurposes.CaseAnalysis, AiCostDriver.Case)]
    [InlineData(AiUsagePurposes.Copilot, AiCostDriver.Question)]
    [InlineData(AiUsagePurposes.PropertyParticulars, AiCostDriver.Assignment)]
    [InlineData(AiUsagePurposes.LitigationPortfolio, AiCostDriver.Assignment)]
    public void Each_purpose_belongs_to_the_thing_that_makes_its_spend_grow(string purpose, AiCostDriver driver) =>
        Assert.Equal(driver, AiCostReportService.DriverOf(purpose));

    [Fact]
    public void An_estimate_is_the_unit_rates_times_the_size_plus_the_fixed_cost()
    {
        var rates = new AiUnitRates(PerFilingDocument: 0.002m, PerOrder: 0.0005m, PerCase: 0.008m, PerQuestion: 0.015m, Fixed: 0.35m, Recorded: new HashSet<AiCostDriver>());
        var estimate = AiCostReportService.Estimate(new AiCostVolumes(FilingDocuments: 100, Orders: 600, Cases: 150, Questions: 20), rates);
        Assert.Equal(0.2m, estimate.FilingDocumentsUsd);
        Assert.Equal(0.3m, estimate.OrdersUsd);
        Assert.Equal(1.2m, estimate.CasesUsd);
        Assert.Equal(0.3m, estimate.QuestionsUsd);
        Assert.Equal(0.35m, estimate.FixedUsd);
        Assert.Equal(2.35m, estimate.TotalUsd);
    }

    // ── recording ─────────────────────────────────────────────────────────

    [Fact]
    public void A_scope_sets_the_assignment_and_purpose_for_the_calls_inside_it_and_restores_them_after()
    {
        Assert.Null(AiUsageContext.RequestId);
        using (AiUsageContext.Begin(42))
        {
            Assert.Equal(42, AiUsageContext.RequestId);
            using (AiUsageContext.Begin(null, "inner"))
            {
                Assert.Equal(42, AiUsageContext.RequestId);   // a scope without a request keeps the enclosing one
                Assert.Equal("inner", AiUsageContext.Purpose);
            }
            Assert.Null(AiUsageContext.Purpose);
        }
        Assert.Null(AiUsageContext.RequestId);
    }

    [Fact]
    public async Task A_recorded_call_carries_the_assignment_and_a_scoped_purpose_overrides_the_clients_default()
    {
        var written = new List<AiUsageRecord>();
        var gate = new TaskCompletionSource();
        AiUsageLog.Configure(r => { lock (written) written.Add(r); gate.TrySetResult(); return Task.CompletedTask; });
        try
        {
            using (AiUsageContext.Begin(7, AiUsagePurposes.EmbeddingOrders))
                AiUsageLog.RecordEmbedding(AiUsagePurposes.Embedding, "gemini-embedding-001", billableCharacters: 4_000);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { AiUsageLog.Configure((Func<AiUsageRecord, Task>?)null); }

        var record = Assert.Single(written);
        Assert.Equal(7, record.RequestId);
        Assert.Equal(AiUsagePurposes.EmbeddingOrders, record.Purpose);
        Assert.Equal(1_000, record.InputTokens);   // about four characters a token when the provider reports no count
        Assert.Equal(4_000, record.BillableCharacters);
    }

    [Fact]
    public async Task A_writer_that_fails_never_surfaces_to_the_caller()
    {
        var attempted = new TaskCompletionSource();
        AiUsageLog.Configure(_ => { attempted.TrySetResult(); throw new InvalidOperationException("the database is down"); });
        try
        {
            AiUsageLog.RecordEmbedding(AiUsagePurposes.Embedding, "gemini-embedding-001", 10);   // must not throw
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { AiUsageLog.Configure((Func<AiUsageRecord, Task>?)null); }
    }

    // ── the report ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_report_prices_each_purpose_and_totals_only_this_assignments_calls()
    {
        var requestId = 9_000_000_000L + Random.Shared.Next(1, 1_000_000);
        await using (var db = CreateContext())
        {
            db.AiUsageRecords.AddRange(
                Call(AiUsagePurposes.CaseAnalysis, "gemini-2.5-flash", 1_000_000, 200_000, 300_000, requestId),   // 0.30 + 1.25
                Call(AiUsagePurposes.CaseAnalysis, "gemini-2.5-flash", 1_000_000, 200_000, 300_000, requestId),
                Call(AiUsagePurposes.OrderClassification, "gemini-2.5-flash-lite", 2_000_000, 500_000, 0, requestId),   // 0.20 + 0.20
                Call(AiUsagePurposes.Copilot, "gemini-2.5-flash", 10_000, 500, 0, requestId + 1));   // someone else's
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var report = await new AiCostReportService(read, Options.Create(Prices)).GetAsync(requestId, CancellationToken.None);

        Assert.True(report.HasUsage);
        Assert.False(report.HasUnpriced);
        Assert.Equal(2, report.Lines.Count);
        var cases = report.Lines.Single(l => l.Purpose == AiUsagePurposes.CaseAnalysis);
        Assert.Equal(2, cases.Calls);
        Assert.Equal(AiCostDriver.Case, cases.Driver);
        Assert.Equal(2 * (0.30m + 1.25m), cases.CostUsd);
        Assert.Equal(0.40m, report.Lines.Single(l => l.Purpose == AiUsagePurposes.OrderClassification).CostUsd);
        Assert.Equal(2 * 1.55m + 0.40m, report.TotalUsd);
        Assert.Equal(report.TotalUsd * Prices.UsdToInr, report.TotalInr);
    }

    [Fact]
    public async Task A_driver_with_no_recorded_spend_keeps_its_estimate_instead_of_reading_zero()
    {
        // only Copilot usage exists for this assignment: the per-document, per-order and per-case rates must stay the built-in estimates
        var requestId = 9_300_000_000L + Random.Shared.Next(1, 1_000_000);
        await using (var db = CreateContext())
        {
            db.AiUsageRecords.Add(Call(AiUsagePurposes.Copilot, "gemini-2.5-flash", 20_000, 500, 0, requestId));
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var rates = await new AiCostReportService(read, Options.Create(Prices)).UnitRatesAsync(CancellationToken.None);

        Assert.Equal(AiCostReportService.DefaultRates.PerFilingDocument, rates.PerFilingDocument);
        Assert.Equal(AiCostReportService.DefaultRates.PerOrder, rates.PerOrder);
        Assert.Equal(AiCostReportService.DefaultRates.PerCase, rates.PerCase);
        Assert.DoesNotContain(AiCostDriver.Case, rates.Recorded);   // and none of them claims to be recorded
    }

    [Fact]
    public async Task A_model_with_no_price_is_flagged_on_the_report_and_counted_as_zero()
    {
        var requestId = 9_100_000_000L + Random.Shared.Next(1, 1_000_000);
        await using (var db = CreateContext())
        {
            db.AiUsageRecords.Add(Call(AiUsagePurposes.Copilot, "mystery-model", 1_000_000, 1_000_000, 0, requestId));
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var report = await new AiCostReportService(read, Options.Create(Prices)).GetAsync(requestId, CancellationToken.None);

        Assert.True(report.HasUnpriced);
        Assert.Equal(0m, report.TotalUsd);
    }

    [Fact]
    public async Task An_assignment_with_no_recorded_usage_says_so()
    {
        await using var read = CreateContext();
        var report = await new AiCostReportService(read, Options.Create(Prices)).GetAsync(9_200_000_000L, CancellationToken.None);
        Assert.False(report.HasUsage);
        Assert.Empty(report.Lines);
        Assert.Equal(0m, report.TotalUsd);
    }
}
