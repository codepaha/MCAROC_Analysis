using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Services.Excel.Parsers;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>What the property is to the case, as the order states it.</summary>
public enum OrderPropertyRole
{
    /// <summary>Its title, possession, delivery, allotment, construction, sale or refund is what the parties dispute.</summary>
    Disputed,
    /// <summary>It is mortgaged or a secured asset that a lender is enforcing or recovering against.</summary>
    Security,
    /// <summary>A court or authority has attached, sealed, auctioned, sold or taken possession of it.</summary>
    Attached,
    /// <summary>It is only the address of a party, advocate or office. Never matched and never shown as a property in dispute.</summary>
    PartyAddress,
    /// <summary>Named for background only; nothing about it is in dispute.</summary>
    Mentioned
}

/// <summary>Whose property it is, as the order states it.</summary>
public enum OrderPropertyRelation { TargetOwned, SoldByTarget, OtherParty, Unknown }

public enum OrderPropertyIssue { Title, Possession, DelayedPossession, Refund, Allotment, Construction, Recovery, Tenancy, Acquisition, Other }

public enum OrderPropertyDirectionKind { Stay, StatusQuo, Injunction, Attachment, Possession, Auction, Sale, Refund, Demolition, Registration, Other }

/// <summary>A direction the order gives about the property, with its verbatim wording.</summary>
public sealed record OrderPropertyDirection(OrderPropertyDirectionKind Kind, string Quote);

/// <summary>One property an order names, read into the same grounded shape as a charge's property (<see cref="AiProperty"/>), so the two
/// can be compared field by field. <see cref="Page"/> is the order page its <see cref="AiProperty.SourceText"/> is on.</summary>
public sealed record OrderProperty(
    int Page, OrderPropertyRole Role, OrderPropertyRelation Relation, OrderPropertyIssue? Issue,
    IReadOnlyList<OrderPropertyDirection> Directions, AiProperty Property);

/// <summary>A property the order acts on without describing it ("sale of the mortgaged properties as described in the schedule to the OA"):
/// it cannot be compared with a charge's property, but a lender enforcing its security against the company is what a credit analyst needs to see.</summary>
public sealed record OrderUnidentifiedProperty(int Page, OrderPropertyRole Role, string Quote);

public sealed record OrderPropertiesResult(IReadOnlyList<OrderProperty> Properties, IReadOnlyList<OrderUnidentifiedProperty>? Unidentified = null);

public sealed record OrderPropertiesValidation(bool IsAccepted, OrderPropertiesResult? Result, IReadOnlyList<string> RejectedFields, string? FailureReason);

/// <summary>One order page sent for reading: its number in the order and its text as stored.</summary>
public sealed record OrderPage(int Number, string Text);

/// <summary>Prompt, response validation and hashing for reading the properties a court order names (#554). The model splits and labels;
/// it may not add, convert or correct anything. Every value is checked against the page it is quoted from with the charge extraction's own
/// checks (<see cref="PropertyParticularsAi.BuildProperty"/>), so an invented survey number, unit or area never reaches a match or the screen.
/// Matching an order's property to a charged property is the portal's own code (<c>OrderPropertyChargeMatcher</c>), not the model's: the charges
/// are not sent, so one reading serves every refresh and every charge filing.</summary>
public static partial class OrderPropertiesAi
{
    public const string PromptVersion = "1.1";
    public const string ModelId = "gemini-3.1-flash-lite";
    internal const int MaxProperties = 20;
    internal const int MaxQuoteChars = 600;
    internal const string PromptMarker = "You are reading the properties named in an Indian court order";

    /// <summary>Order pages that may name a property: the only ones sent. A page with none of these words cannot describe a property well
    /// enough to compare (a bare "the suit property" is dropped anyway), so leaving it out costs nothing that could be matched.</summary>
    public static bool MayNameProperty(string pageText) => PropertyCue().IsMatch(pageText);

    /// <summary>The same pages (whitespace-insensitive) under the same prompt share one reading.</summary>
    public static string HashOf(IReadOnlyList<OrderPage> pages)
    {
        var canonical = PromptVersion + "\n" + string.Join("\n", pages.Select(p => $"{p.Number}:{PropertyParticularsAi.Whitespace().Replace(p.Text.Trim(), " ")}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <param name="targetNames">The company's registered and former names: the model says whether a property is the company's, or one it sold.</param>
    public static string BuildPrompt(IReadOnlyCollection<string> targetNames, string? court, string? caseType,
        IReadOnlyCollection<string> petitioners, IReadOnlyCollection<string> respondents, IReadOnlyList<OrderPage> pages) => $$"""
        {{PromptMarker}}, for a BFSI credit analyst checking which properties of a company are under litigation.
        The company examined (the "target"): {{string.Join(" / ", targetNames)}}.
        Court: {{court ?? "not stated"}}. Case type: {{caseType ?? "not stated"}}.
        Petitioners: {{(petitioners.Count == 0 ? "not stated" : string.Join("; ", petitioners))}}.
        Respondents: {{(respondents.Count == 0 ? "not stated" : string.Join("; ", respondents))}}.

        List every specific immovable property the order pages below name: a flat, unit, shop, office, floor, building, project, plot,
        land parcel (survey / CTS / khasra / gat number), house or premises. One entry per distinct property. The same property named on
        several pages is ONE entry, quoted from the page that describes it most fully.

        STRICT RULES:
        - Copy every value exactly as written. Never convert units, never correct spelling, never add a value that is not written.
        - If a field is not stated, use null (or [] for lists). Do not guess, and do not fill a field from your own knowledge of a place.
        - sourceText: quote verbatim the clause of that page that describes the property. Every unit number, floor, area and survey/CTS/
          khasra/gat/plot number of the property must be inside its own sourceText. page: the page number of that quote.
        - Leave out a property the order describes only as "the suit property", "the said flat", "the premises" or the like, with no number,
          building, project or place: it cannot be identified.
        - role:
          Disputed     = the court is asked to decide something about this property itself: its title, possession, delivery, allotment,
                         construction, sale or a refund for it. Not when the property is only the background of another claim
                         (defamation, a criminal complaint, a service or tax matter): then it is Mentioned;
          Security     = it is mortgaged or a secured asset a lender is enforcing or recovering against (SARFAESI, DRT, recovery suit);
          Attached     = a court or authority has attached, sealed, auctioned, sold or taken possession of it;
          PartyAddress = it is only the address of a party, an advocate or an office (cause titles, "having its registered office at",
                         "residing at"). Always use PartyAddress for those, even when the address has a plot number;
          Mentioned    = named for background only.
        - relation: TargetOwned (the order says the target owns, holds or develops it), SoldByTarget (the target sold, allotted or agreed
          to sell it to someone, e.g. a home buyer's flat in the target's project), OtherParty (someone else's), Unknown.
        - issue: what is in dispute about it — {{string.Join("|", Enum.GetNames<OrderPropertyIssue>())}} — or null when role is not Disputed.
        - directions: each direction this order gives about this property, with a verbatim quote of at most 60 words — kind:
          {{string.Join("|", Enum.GetNames<OrderPropertyDirectionKind>())}}. [] when the order directs nothing about it.
        - kind: {{string.Join("|", Enum.GetNames<PropertyKind>().Where(k => k is nameof(PropertyKind.Premises) or nameof(PropertyKind.Land) or nameof(PropertyKind.BuildingOrProject) or nameof(PropertyKind.Parking)))}}.
          area unit: {{string.Join("|", Enum.GetNames<AiAreaUnit>())}}. basis: {{string.Join("|", Enum.GetNames<AreaBasis>())}}.
          scheme: CTS|Survey|Plot|Gat|Khasra. qualifier: New|Old|null.
        - owner: only when the order names who owns it.
        - unidentified: ALWAYS fill this when the order directs or records something about properties it does not describe ("sale of the mortgaged
          properties as described in the schedule", "the secured assets", "attachment of the properties of the defendant"), one entry each
          with role Security, Attached or Disputed and a verbatim quote of at most 60 words. Not for a property listed in "properties".
        Respond only with JSON of this shape ({"properties":[],"unidentified":[]} when the pages name no property):
        {"properties":[{"page":1,"sourceText":"exact quote","role":"Disputed","relation":"SoldByTarget","issue":"DelayedPossession",
          "directions":[{"kind":"Possession","quote":"exact quote"}],
          "kind":"Premises","owner":null,"unitNumber":null,"floor":null,"building":null,"project":null,
          "areas":[{"value":0,"unit":"SqFt","basis":"SuperBuiltUp","equivalentValue":null,"equivalentUnit":null}],
          "surveyNumbers":[{"scheme":"Survey","qualifier":null,"numbers":["75A"]}],"localities":[],"village":null,"taluka":null,
          "district":null,"city":null,"state":null,"pin":null}],
         "unidentified":[{"page":1,"role":"Security","quote":"exact quote"}]}

        Order pages:
        {{string.Join("\n", pages.Select(p => $"--- Page {p.Number} ---\n{Normalize(p.Text)}"))}}
        """;

    public static OrderPropertiesValidation Validate(string rawJson, IReadOnlyList<OrderPage> pages)
    {
        Response? response;
        try { response = JsonSerializer.Deserialize<Response>(PropertyParticularsAi.UnwrapSingleElementArray(rawJson), PropertyParticularsAi.JsonOptions); }
        catch (JsonException ex) { return Failed($"Invalid JSON: {ex.Message}"); }
        if (response?.Properties is null) return Failed("Response has no properties array.");
        if (response.Properties.Count > MaxProperties) return Failed($"Response named {response.Properties.Count} properties (max {MaxProperties}).");

        var texts = pages.ToDictionary(p => p.Number, p => PropertyParticularsAi.Whitespace().Replace(Normalize(p.Text), " "));
        var groundings = texts.ToDictionary(kv => kv.Key, kv => new PropertyParticularsAi.Grounding(kv.Value));
        var rejected = new List<string>();
        var properties = new List<OrderProperty>();
        var claimed = new Dictionary<int, List<(int Start, int End)>>();
        for (var i = 0; i < response.Properties.Count; i++)
        {
            var p = response.Properties[i];
            var at = $"properties[{i}]";
            if (p.Page is not { } pageNumber || !groundings.TryGetValue(pageNumber, out var page))
            {
                rejected.Add($"{at}: page {p.Page} was not sent — property dropped");
                continue;
            }
            if (!PropertyParticularsAi.TryName<PropertyKind>(p.Kind, out var kind)
                || kind is not (PropertyKind.Premises or PropertyKind.Land or PropertyKind.BuildingOrProject or PropertyKind.Parking))
            {
                rejected.Add($"{at}: unsupported kind '{p.Kind}' — property dropped");
                continue;
            }
            if (FindQuote(texts[pageNumber], p.SourceText) is not { } span)
            {
                rejected.Add($"{at}: sourceText is not a verbatim quote of page {pageNumber} — property dropped");
                continue;
            }
            var spans = claimed.TryGetValue(pageNumber, out var list) ? list : claimed[pageNumber] = [];
            if (spans.Any(c => span.Start < c.End && c.Start < span.End))
            {
                rejected.Add($"{at}: sourceText overlaps another property's clause — property dropped");
                continue;
            }
            spans.Add(span);
            var clause = texts[pageNumber][span.Start..span.End];
            // "560 033": the PIN as the order writes it, read as one (the page text is joined the same way by Normalize).
            p = p with { Pin = p.Pin is null ? null : PropertyParticularsAi.Whitespace().Replace(p.Pin, "") };
            var property = PropertyParticularsAi.BuildProperty(p, PropertyAssetClass.Immovable, kind, page, new PropertyParticularsAi.Grounding(clause), clause, at, rejected);
            if (!Identifiable(property))
            {
                rejected.Add($"{at}: names no unit, survey number, building, project or place that survived the check — property dropped");
                continue;
            }

            var role = PropertyParticularsAi.TryName<OrderPropertyRole>(p.Role, out var r) ? r : OrderPropertyRole.Mentioned;
            if (p.Role is not null && role == OrderPropertyRole.Mentioned && !string.Equals(p.Role, nameof(OrderPropertyRole.Mentioned), StringComparison.OrdinalIgnoreCase))
                rejected.Add($"{at}.role: unsupported '{p.Role}', read as Mentioned");
            var relation = PropertyParticularsAi.TryName<OrderPropertyRelation>(p.Relation, out var rel) ? rel : OrderPropertyRelation.Unknown;
            OrderPropertyIssue? issue = role == OrderPropertyRole.Disputed && PropertyParticularsAi.TryName<OrderPropertyIssue>(p.Issue, out var iss) ? iss : null;

            var directions = new List<OrderPropertyDirection>();
            foreach (var d in p.Directions ?? [])
            {
                if (!PropertyParticularsAi.TryName<OrderPropertyDirectionKind>(d.Kind, out var dk)) { rejected.Add($"{at}.directions: unsupported kind '{d.Kind}'"); continue; }
                if (string.IsNullOrWhiteSpace(d.Quote) || d.Quote.Length > MaxQuoteChars || FindQuote(texts[pageNumber], d.Quote) is not { } q)
                {
                    rejected.Add($"{at}.directions: {dk} quote is not verbatim on page {pageNumber}");
                    continue;
                }
                directions.Add(new OrderPropertyDirection(dk, texts[pageNumber][q.Start..q.End]));
            }
            properties.Add(new OrderProperty(pageNumber, role, relation, issue, directions, property));
        }

        var unidentified = new List<OrderUnidentifiedProperty>();
        foreach (var u in response.Unidentified ?? [])
        {
            if (u.Page is not { } n || !groundings.TryGetValue(n, out var page)
                || !PropertyParticularsAi.TryName<OrderPropertyRole>(u.Role, out var role)
                || role is not (OrderPropertyRole.Security or OrderPropertyRole.Attached or OrderPropertyRole.Disputed)
                || string.IsNullOrWhiteSpace(u.Quote) || u.Quote.Length > MaxQuoteChars || FindQuote(texts[n], u.Quote) is not { } q)
            {
                rejected.Add($"unidentified: page {u.Page} {u.Role} quote dropped (page not sent, unsupported role, or not verbatim)");
                continue;
            }
            unidentified.Add(new OrderUnidentifiedProperty(n, role, texts[n][q.Start..q.End]));
        }

        // An empty answer is accepted: the pages name no property.
        if (properties.Count == 0 && unidentified.Count == 0 && response.Properties.Count + (response.Unidentified?.Count ?? 0) > 0)
            return new OrderPropertiesValidation(false, null, rejected, "No property survived validation.");
        return new OrderPropertiesValidation(true, new OrderPropertiesResult(properties, unidentified), rejected, null);
    }

    /// <summary>Enough to tell this property from another: a unit, a survey/CTS/khasra/gat/plot number, a building or project, or a place.</summary>
    internal static bool Identifiable(AiProperty p) =>
        p.UnitNumber is not null || p.SurveyNumbers.Count > 0 || p.Building is not null || p.Project is not null
        || p.Localities.Count > 0 || p.Village is not null || p.City is not null || p.Pin is not null;

    public static string Serialize(OrderPropertiesResult result) => JsonSerializer.Serialize(result, PropertyParticularsAi.JsonOptions);

    public static OrderPropertiesResult? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<OrderPropertiesResult>(json, PropertyParticularsAi.JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static OrderPropertiesValidation Failed(string reason) => new(false, null, [], reason);

    private sealed record Response(List<ResponseOrderProperty>? Properties, List<ResponseUnidentified>? Unidentified);

    private sealed record ResponseUnidentified(int? Page, string? Role, string? Quote);

    private const int MinQuoteLetters = 12;

    /// <summary>Where a quote sits on the page, comparing letters and digits only. Scanned orders run words together ("Plotno.304-0,roadno.78")
    /// and the model writes them apart again; every letter and digit of the quote must still be on the page, in order and adjacent, so
    /// nothing can be added or changed — only spacing and punctuation are let go. The span returned is the page's own wording.</summary>
    internal static (int Start, int End)? FindQuote(string page, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var needle = new string(quote.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (needle.Length < MinQuoteLetters) return null;
        var map = new List<int>(page.Length);
        var hay = new StringBuilder(page.Length);
        for (var i = 0; i < page.Length; i++)
            if (char.IsLetterOrDigit(page[i])) { hay.Append(char.ToLowerInvariant(page[i])); map.Add(i); }
        var text = hay.ToString();
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        if (at >= 0) return (map[at], map[at + needle.Length - 1] + 1);
        // Scanned text misreads letters ("craim", "Appricant") and the model quotes them corrected. A quote that differs from a run of the
        // page by at most MaxQuoteEditShare of its letters is that run; the values inside it are still checked exactly
        // (PropertyParticularsAi.BuildProperty), so a corrected letter can never bring a number the page does not have.
        return ApproximateSpan(text, needle, (int)Math.Max(2, needle.Length * MaxQuoteEditShare)) is { } s ? (map[s.Start], map[s.End - 1] + 1) : null;
    }

    private const double MaxQuoteEditShare = 0.06;

    /// <summary>The run of <paramref name="hay"/> closest to <paramref name="needle"/> by edit distance (Sellers' approximate substring
    /// match), when that distance is at most <paramref name="maxEdits"/>; the earliest such run on a tie.</summary>
    internal static (int Start, int End)? ApproximateSpan(string hay, string needle, int maxEdits)
    {
        var m = needle.Length;
        if (m == 0 || hay.Length == 0) return null;
        // cost[i] = edits to match needle[..i] ending at the current hay position; start[i] = where that match began.
        var cost = new int[m + 1];
        var start = new int[m + 1];
        var prevCost = new int[m + 1];
        var prevStart = new int[m + 1];
        for (var i = 0; i <= m; i++) { prevCost[i] = i; prevStart[i] = 0; }
        int best = int.MaxValue, bestEnd = -1, bestStart = -1;
        for (var j = 1; j <= hay.Length; j++)
        {
            cost[0] = 0;
            start[0] = j;
            for (var i = 1; i <= m; i++)
            {
                var sub = prevCost[i - 1] + (needle[i - 1] == hay[j - 1] ? 0 : 1);
                var del = prevCost[i] + 1;
                var ins = cost[i - 1] + 1;
                if (sub <= del && sub <= ins) { cost[i] = sub; start[i] = prevStart[i - 1]; }
                else if (del <= ins) { cost[i] = del; start[i] = prevStart[i]; }
                else { cost[i] = ins; start[i] = start[i - 1]; }
            }
            if (cost[m] < best) { best = cost[m]; bestEnd = j; bestStart = start[m]; }
            (prevCost, cost) = (cost, prevCost);
            (prevStart, start) = (start, prevStart);
        }
        return best <= maxEdits && bestEnd > bestStart ? (bestStart, bestEnd) : null;
    }

    /// <summary>Scanned orders write quotes and dashes many ways (` ‘ ’ “ ” – —) and the model copies them back plainly; both sides are read
    /// with one form, so a faithful quote is not refused over a quote mark. A PIN written "560 033" after a dash or "PIN" is joined.</summary>
    public static string Normalize(string text)
    {
        var t = text.Replace('`', '\'').Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u00B4', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u2013', '-').Replace('\u2014', '-');
        return SpacedPin().Replace(t, "${pre}${a}${b}");
    }

    [GeneratedRegex(@"(?<pre>(?:[-_]|pin(?:\s*code)?\s*[:.-]?)\s*)(?<a>[1-9]\d{2})\s(?<b>\d{3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SpacedPin();

    private sealed record ResponseDirection(string? Kind, string? Quote);

    /// <summary>The charge extraction's property fields plus what only an order says.</summary>
    private sealed record ResponseOrderProperty(
        int? Page, string? Role, string? Relation, string? Issue, List<ResponseDirection>? Directions,
        string? SourceText, string? AssetClass, string? Kind, string? Owner, string? UnitNumber, string? Floor, string? Building, string? Project,
        List<PropertyParticularsAi.ResponseArea>? Areas, int? ParkingSpaces, List<PropertyParticularsAi.ResponseSurvey>? SurveyNumbers, List<string>? Localities,
        string? Village, string? Taluka, string? District, string? City, string? State, string? Pin)
        : PropertyParticularsAi.ResponseProperty(SourceText, AssetClass, Kind, Owner, UnitNumber, Floor, Building, Project, Areas, ParkingSpaces,
            SurveyNumbers, Localities, Village, Taluka, District, City, State, Pin);

    // Tuned on the 2026-10-26 readings of Godrej and Coastal (1,755 orders sent with bare words such as "office", "floor", "schedule"): a numbered
    // unit, plot or survey ("flat no", "gut no"), plural units, an area, or wording about property in dispute or under security. It sends 318 of
    // those orders and keeps 164 of the 171 pages that named a property; a cause-title address ("Godrej One, 5th Floor") alone no longer sends
    // a page. Short words keep their boundaries ("gat" is inside "against"); the phrases match inside words too, as scanned orders run words
    // together ("theschedule", "mortgagedproperties").
    [GeneratedRegex(@"\b(?:flat|apartment|villa|bungalow|shop|unit|plot|building|bldg|tower|wing|survey|s\.|sy\.?|c\.?t\.?s|khasra|khata|gat|gut|hissa|cts)\s*\.?\s*(?:no|nos|number|bearing)\b"
        + @"|\b(?:flats|plots|units|apartments|villas|shops)\b|\bkhasra\b|\bguntha|\bacres?\b|\bhectares?\b|admeasuring"
        + @"|land\s*bearing|(?:plot|piece|parcel)\s+of\s+land|lis\s*pendens|conveyance|allotment|possession\s+of|sale\s+deed|development\s+agreement|agreement\s+for\s+(?:sale|development)"
        + @"|project\s+(?:named|called|known)|construction\s+called|housing\s+society"
        + @"|schedule\s*(?:property|properties|to\s+the|to\s+o\.?a)|scheduled\s+propert|mortgag\w*\s*propert|secured\s*assets?"
        + @"|suit\s*(?:property|premises|flat|land)|subject\s*(?:property|premises|flat|land|plots?)|said\s*(?:flat|apartment|unit|plot|land|premises)"
        + @"|sq\.?\s?(?:ft|feet|mtrs?|meters?|metres?|yds?|yards?)\b|square\s*(?:feet|met)"
        + @"|propert\w*\s*mortgaged|mortgag\w*\s*(?:&|and)?\s*(?:creating\s*charge\s*on\s*)?(?:of|on|over)?\s*(?:all\s*)?(?:the\s*)?(?:borrower'?s?\s*)?(?:immovable\s*)?propert"
        + @"|hypothecat\w*\s*(?:/|and|&)?\s*mortgag|described\s*in\s*(?:the\s*)?schedul|immovable\s*propert|\bauction|site\s*address|project\s*site|\bsector\s*-?\s*\d+",
        RegexOptions.IgnoreCase)]
    private static partial Regex PropertyCue();
}
