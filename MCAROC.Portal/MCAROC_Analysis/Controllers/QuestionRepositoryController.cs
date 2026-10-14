using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>The cross-client question repository (#499): what clients ask, grouped by meaning, how often, by how many clients, and how often the answer
/// had evidence. Masked text only. Full access only: it spans clients.</summary>
public class QuestionRepositoryController(AppDbContext db) : Controller
{
    private const int PageSize = 100;
    private const int ExamplesPerGroup = 3;

    [HttpGet("/Questions")]
    public async Task<IActionResult> Index(string? sort, CancellationToken ct)
    {
        if (!PortalAccess.HasFullAccess(User)) return NotFound();

        var groups = db.ChatQuestionGroups.AsNoTracking();
        groups = sort switch
        {
            // The gaps: asked often, rarely answered with evidence.
            "gaps" => groups.OrderBy(g => (double)g.AnsweredWithEvidenceCount / g.AskedCount).ThenByDescending(g => g.AskedCount),
            "recent" => groups.OrderByDescending(g => g.LastAskedUtc),
            _ => groups.OrderByDescending(g => g.ClientCount).ThenByDescending(g => g.AskedCount)
        };
        var rows = await groups.Take(PageSize).Select(g => new QuestionGroupRow(
            g.ChatQuestionGroupId, g.CanonicalQuestion, g.AskedCount, g.ClientCount, g.AnsweredWithEvidenceCount, g.AnsweredFromIndexCount,
            g.FirstAskedUtc, g.LastAskedUtc,
            g.Entries.Where(e => e.MaskedText != g.CanonicalQuestion).Select(e => e.MaskedText).Distinct().Take(ExamplesPerGroup).ToList()))
            .ToListAsync(ct);
        var totals = new QuestionRepositoryTotals(
            await db.ChatQuestionEntries.CountAsync(ct), await db.ChatQuestionGroups.CountAsync(ct));
        return View(new QuestionRepositoryPage(rows, totals, sort is "gaps" or "recent" ? sort : "clients"));
    }
}

public sealed record QuestionGroupRow(long GroupId, string Question, int Asked, int Clients, int WithEvidence, int FromIndex,
    DateTime FirstAskedUtc, DateTime LastAskedUtc, IReadOnlyList<string> Examples)
{
    public double EvidenceShare => Asked == 0 ? 0 : (double)WithEvidence / Asked;
}

public sealed record QuestionRepositoryTotals(int Questions, int Groups);

public sealed record QuestionRepositoryPage(IReadOnlyList<QuestionGroupRow> Groups, QuestionRepositoryTotals Totals, string Sort);
