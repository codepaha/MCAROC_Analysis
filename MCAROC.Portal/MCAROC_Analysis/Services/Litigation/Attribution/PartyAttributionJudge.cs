using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

/// <summary>The company the parties are judged against.</summary>
public sealed record AttributionSubject(string LegalName, IReadOnlyList<string> FormerNames, string? Identifier, string? RegisteredOffice);

public sealed record PartyJudgement(string Party, LitigationPartyVerdictKind Verdict, string Reason);

/// <summary>
/// The judgement for party names the rules could not settle (#489, decision a; owner: an AI judges whether the party is the company). One call per batch of
/// distinct names. The answer for a name counts only if it quotes that name exactly as given; a name left out, misquoted or with an unknown verdict counts
/// as <see cref="LitigationPartyVerdictKind.Unsure"/>, never as the company.
/// </summary>
public static class PartyAttributionJudge
{
    public const string PromptMarker = "MCAROC PARTY ATTRIBUTION";
    public const string PromptVersion = "party-attribution-v1";
    public const int BatchSize = 40;

    public static string BuildPrompt(AttributionSubject subject, IReadOnlyList<string> parties)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{PromptMarker} ({PromptVersion})");
        sb.AppendLine("You decide whether each party name below, taken from Indian court records, is the company described here.");
        sb.AppendLine();
        sb.AppendLine($"Company: {subject.LegalName}");
        if (subject.FormerNames.Count > 0) sb.AppendLine($"Former names: {string.Join("; ", subject.FormerNames)}");
        if (!string.IsNullOrWhiteSpace(subject.Identifier)) sb.AppendLine($"Corporate identity number: {subject.Identifier}");
        if (!string.IsNullOrWhiteSpace(subject.RegisteredOffice)) sb.AppendLine($"Registered office: {subject.RegisteredOffice}");
        sb.AppendLine();
        sb.AppendLine("""
            For each party answer:
            - "same": the party is this company, however it is written: a misspelling, a town or branch after the name, a transliteration or Hindi
              spelling, a former name, the company acting through its director, liquidator or resolution professional.
            - "different": another company, body or person. A person is "different" even when described by the company's name
              (for example its director or former director). A group or associated company with a different name is "different".
            - "unsure": the name alone cannot tell.
            Quote each party exactly as given. Reason in at most 20 words.
            Answer with JSON only: {"verdicts": [{"party": "...", "verdict": "same|different|unsure", "reason": "..."}]}

            Parties:
            """);
        for (var i = 0; i < parties.Count; i++) sb.AppendLine($"{i + 1}. {parties[i]}");
        return sb.ToString();
    }

    /// <summary>One judgement per party asked about, in the order asked; the answer's own text is trusted only where it quotes an asked name.</summary>
    public static IReadOnlyList<PartyJudgement> Read(IReadOnlyList<string> asked, string raw)
    {
        var answers = new Dictionary<string, (LitigationPartyVerdictKind Verdict, string Reason)>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1 && root[0].ValueKind == JsonValueKind.Object) root = root[0];
            var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("verdicts", out var v) ? v : root;
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var party = Str(e, "party");
                    var verdict = Str(e, "verdict").Trim().ToLowerInvariant() switch
                    {
                        "same" => LitigationPartyVerdictKind.Same,
                        "different" => LitigationPartyVerdictKind.Different,
                        _ => LitigationPartyVerdictKind.Unsure
                    };
                    var reason = Str(e, "reason").Trim();
                    if (reason.Length > 300) reason = reason[..300];
                    answers.TryAdd(Key(party), (verdict, reason));
                }
        }
        catch (JsonException) { }

        return asked.Select(p => answers.TryGetValue(Key(p), out var a)
                ? new PartyJudgement(p, a.Verdict, a.Reason.Length > 0 ? a.Reason : "No reason given.")
                : new PartyJudgement(p, LitigationPartyVerdictKind.Unsure, "The judgement did not quote this party exactly."))
            .ToList();

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    }

    /// <summary>A quote matches when it is the asked name, ignoring case and runs of spaces.</summary>
    private static string Key(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
