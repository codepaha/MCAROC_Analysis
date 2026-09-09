using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record StructuredFact(string DomainKey, string Text, string? EntityType, long? EntityId);

/// <summary>Builds a source-traceable structured-facts digest from Phase 1 entities for one request.
/// Broadened beyond a single "latest 2 years, current directors" snapshot — includes current AND historical
/// directors, 3-5 years of financials, open AND recently-satisfied charges — so a question like "who
/// resigned in 2022?" or "what was FY2022 revenue?" is actually answerable from the digest, not just the
/// newest state. Reuses QuestionHintExtractor's output to decide *emphasis*, not exclusion: a domain the
/// hints flag as relevant gets full per-row detail; every other domain still gets at least a one-line
/// headline (never dropped entirely) so the digest never goes silent on a domain the hints didn't catch.</summary>
public class StructuredFactsProvider(AppDbContext db)
{
    private static readonly string[] AllDomains = ["Directors", "Financial", "Charges", "Gst", "Epfo", "Litigation"];

    public async Task<List<StructuredFact>> BuildDigestAsync(long requestId, QuestionHints hints, CancellationToken ct)
    {
        var request = await db.Requests.FirstAsync(r => r.RequestId == requestId, ct);
        var facts = new List<StructuredFact>();
        if (request.LatestCompletedIngestionRunId is not { } ingestionRunId)
            return facts;

        var detailed = DetermineDetailedDomains(hints);

        var profile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);
        if (profile is not null)
            facts.Add(new StructuredFact("CompanyProfile",
                $"{profile.CompanyName} | CIN: {profile.Cin} | Status: {profile.CompanyStatus} | Compliance: {profile.ComplianceStatus} | Incorporated: {profile.IncorporationDate}",
                "CompanyProfile", profile.CompanyProfileId));

        var directors = await db.Directors.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (directors.Count > 0)
        {
            if (detailed.Contains("Directors"))
                foreach (var d in directors)
                    facts.Add(new StructuredFact("Directors",
                        $"Director: {d.NameRaw} ({d.Designation}) | Appointed: {d.OriginalAppointmentDate} | "
                            + (d.CessationDate is null ? "Current" : $"Ceased: {d.CessationDate}"),
                        "Director", d.DirectorId));
            else
            {
                var current = directors.Count(d => d.CessationDate is null);
                facts.Add(new StructuredFact("Directors",
                    $"{current} current director(s), {directors.Count - current} historical (ceased) director(s) on record.", null, null));
            }
        }

        // Standalone is the default basis. Every financial fact is tagged with its basis so a question
        // about "consolidated revenue" resolves to the consolidated figure and an untagged question to
        // standalone.
        var standalone = await db.FinancialYearData
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone)
            .OrderByDescending(x => x.FinancialYear).Take(5).ToListAsync(ct);
        if (standalone.Count > 0)
        {
            if (detailed.Contains("Financial"))
                foreach (var f in standalone)
                    facts.Add(new StructuredFact("Financial",
                        $"FY{f.FinancialYear} (Standalone): Revenue ₹{f.Revenue} Cr, EBITDA ₹{f.Ebitda} Cr, PAT ₹{f.Pat} Cr, Net Worth ₹{f.NetWorth} Cr, Total Debt ₹{f.TotalDebt} Cr",
                        "FinancialYearData", f.FinancialId));
            else
            {
                var latest = standalone[0];
                facts.Add(new StructuredFact("Financial",
                    $"Latest standalone financials: FY{latest.FinancialYear} revenue ₹{latest.Revenue} Cr, PAT ₹{latest.Pat} Cr. ({standalone.Count} year(s) of data on record.)",
                    "FinancialYearData", latest.FinancialId));
            }
        }

        var consolidated = await db.FinancialYearData
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Consolidated)
            .OrderByDescending(x => x.FinancialYear).Take(5).ToListAsync(ct);
        foreach (var f in consolidated)
            facts.Add(new StructuredFact("Financial",
                $"FY{f.FinancialYear} (Consolidated): Revenue ₹{f.Revenue} Cr, EBITDA ₹{f.Ebitda} Cr, PAT ₹{f.Pat} Cr, Net Worth ₹{f.NetWorth} Cr, Total Debt ₹{f.TotalDebt} Cr",
                "FinancialYearData", f.FinancialId));

        var charges = await db.RocCharges.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (charges.Count > 0)
        {
            var recentCutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2));
            var relevant = charges.Where(c => c.SatisfactionDate is null || c.SatisfactionDate >= recentCutoff).ToList();
            // Detailed per-row only when there's at least one open/recent charge to show; otherwise fall
            // through to the headline so the Charges domain is never dropped from the digest entirely
            // (e.g. a charge-specific question on a company whose charges were all satisfied >2y ago).
            if (detailed.Contains("Charges") && relevant.Count > 0)
                foreach (var c in relevant)
                    facts.Add(new StructuredFact("Charges",
                        $"Charge {c.RocChargeNumber}: Holder {c.LatestChargeHolderRaw}, Amount ₹{c.CurrentAmount} Cr, Status {c.ChargeStatus}"
                            + (c.SatisfactionDate is { } sd ? $", Satisfied {sd}" : ""),
                        "RocCharge", c.ChargeId));
            else
            {
                var open = charges.Count(c => c.SatisfactionDate is null);
                facts.Add(new StructuredFact("Charges", $"{open} open charge(s), {charges.Count - open} satisfied charge(s) on record.", null, null));
            }
        }

        var gstRegs = await db.GstRegistrations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (gstRegs.Count > 0)
        {
            if (detailed.Contains("Gst"))
                foreach (var g in gstRegs)
                    facts.Add(new StructuredFact("Gst", $"GSTIN {g.Gstin} ({g.State}): {g.Status}", "GstRegistration", g.GstId));
            else
                facts.Add(new StructuredFact("Gst",
                    $"{gstRegs.Count(g => g.CancellationDate is null)} active GST registration(s) of {gstRegs.Count} on record.", null, null));
        }

        var epfo = await db.EpfoContributions.Where(x => x.IngestionRunId == ingestionRunId)
            .OrderByDescending(x => x.WageMonth).Take(6).ToListAsync(ct);
        if (epfo.Count > 0)
        {
            if (detailed.Contains("Epfo"))
                foreach (var e in epfo)
                    facts.Add(new StructuredFact("Epfo",
                        $"{e.WageMonth}: {e.EmployeeCount} employees, ₹{e.ContributionAmountCrore} Cr, {e.PaymentStatus}", "EpfoContribution", e.EpfoId));
            else
                facts.Add(new StructuredFact("Epfo", $"EPFO contribution data available for {epfo.Count} recent month(s) on record.", null, null));
        }

        var litigations = await db.Litigations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (litigations.Count > 0)
        {
            if (detailed.Contains("Litigation"))
                foreach (var l in litigations)
                    facts.Add(new StructuredFact("Litigation",
                        $"{l.CaseType} ({l.CaseStatus}): {l.Litigants}, Case No. {l.CaseNumber}", "Litigation", l.LitigationId));
            else
                facts.Add(new StructuredFact("Litigation", $"{litigations.Count} litigation record(s) on file.", null, null));
        }

        return facts;
    }

    private static HashSet<string> DetermineDetailedDomains(QuestionHints hints)
    {
        // No hint signal at all: keep today's full breadth rather than guessing what's relevant.
        if (!hints.HasSoftHints)
            return [.. AllDomains];

        var set = new HashSet<string>();
        if (hints.Category == FilingCategory.Charge) set.Add("Charges");
        if (hints.Category == FilingCategory.Financial) set.Add("Financial");
        if (hints.Category == FilingCategory.Compliance)
        {
            set.Add("Directors");
            set.Add("Gst");
            set.Add("Epfo");
            set.Add("Litigation");
        }
        if (hints.LenderNameKeyword is not null) set.Add("Charges");
        if (hints.Year is not null) set.Add("Financial");

        // A hint fired but matched nothing domain-specific (e.g. only a form-type token) — still show a
        // sane default core rather than leaving every domain at headline level.
        if (set.Count == 0)
        {
            set.Add("Directors");
            set.Add("Financial");
        }
        return set;
    }
}
