using MCAROC_Analysis.Services;

namespace MCAROC_Analysis.Tests;

/// <summary>Every displayed time is IST (owner, 2026-09-24), independent of the server's clock zone.</summary>
public sealed class IstTests
{
    [Fact]
    public void Stored_utc_is_shown_as_ist_with_the_label()
    {
        var utc = new DateTime(2026, 9, 24, 3, 8, 34, DateTimeKind.Unspecified); // what EF hands back
        Assert.Equal("24 Sep 2026 08:38 IST", Ist.Format(utc));
        Assert.Equal("08:38:34 IST", Ist.Format(utc, "HH:mm:ss"));
    }

    [Fact]
    public void Late_utc_evening_is_the_next_day_in_ist()
    {
        var utc = new DateTime(2026, 12, 31, 18, 45, 0, DateTimeKind.Utc);
        Assert.Equal("1 Jan 2027", Ist.Date(utc));
        Assert.Equal("1 Jan 2027 00:15 IST", Ist.Format(utc));
    }

    [Fact]
    public void Local_kind_values_are_converted_from_the_server_zone_first()
    {
        var utc = new DateTime(2026, 9, 24, 3, 8, 0, DateTimeKind.Utc);
        Assert.Equal(Ist.Format(utc), Ist.Format(utc.ToLocalTime()));
    }

    [Fact]
    public void Offsets_and_nulls_are_handled()
    {
        Assert.Equal("24 Sep 2026 08:38 IST", Ist.Format(new DateTimeOffset(2026, 9, 24, 3, 8, 0, TimeSpan.Zero)));
        Assert.Equal("24 Sep 2026 08:38 IST", Ist.Format((DateTimeOffset?)new DateTimeOffset(2026, 9, 24, 3, 8, 0, TimeSpan.Zero)));
        Assert.Equal("—", Ist.Format((DateTimeOffset?)null));
        Assert.Equal("—", Ist.Format((DateTime?)null));
        Assert.Equal("N/A", Ist.Format((DateTime?)null, whenNull: "N/A"));
        Assert.Equal("-", Ist.Date((DateTime?)null, "yyyy-MM-dd", "-"));
    }

    [Fact]
    public void Now_uses_the_given_clock()
    {
        var clock = new FixedTime(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 9, 24, 1, 30, 0), Ist.Now(clock));
    }

    /// <summary>Guard for the next change: no source file may print a timestamp in UTC or server-local time.
    /// Analyst* files are Codex's (#270) and are converted separately; DateTimeNormalizer parses input, it doesn't display.</summary>
    [Fact]
    public void No_source_file_displays_a_timestamp_outside_ist()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        var app = Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis");
        var patterns = new System.Text.RegularExpressions.Regex[]
        {
            new(@"ToLocalTime\(\)"),                  // server-local, whatever zone the host is in
            new(@"\{[^{}""]+:[uOoR]\}"),              // "2026-09-24 03:08:34Z"-style interpolation
            new(@"[}\)]\s*UTC\b(?!C)"),               // "{x:...} UTC" / "@(x) UTC" labels
            new(@"ToString\(""[^""]*UTC"),            // "HH:mm UTC" formats
            new(@"DateTime\.Now\b"),                  // server-local now
            new(@"(?i)(date|time|created|utc|started|completed)\w*\.toLocale(Date|Time)?String\(|getHours\(\)") // browser-local rendering
        };
        var offenders = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".cshtml") || (f.EndsWith(".js") && f.Contains($"{Path.DirectorySeparatorChar}js{Path.DirectorySeparatorChar}")))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                && !Path.GetFileName(f).StartsWith("Analyst")
                && !f.Contains($"{Path.DirectorySeparatorChar}Analyst")
                && !f.EndsWith("DateTimeNormalizer.cs"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && !x.line.TrimStart().StartsWith("///") && !x.line.TrimStart().StartsWith("*")
                && !x.line.Contains("Log") // log templates are for developers, not users
                && patterns.Any(p => p.IsMatch(x.line)))
            // The Node-test fallbacks keep the old formatting only where window.mcaIst doesn't exist.
            .Where(x => !x.line.Contains("mcaIst") && !x.line.TrimStart().StartsWith(": dateObj.getHours()"))
            .Select(x => $"{Path.GetRelativePath(app, x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Timestamps shown outside IST:\n" + string.Join("\n", offenders));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
