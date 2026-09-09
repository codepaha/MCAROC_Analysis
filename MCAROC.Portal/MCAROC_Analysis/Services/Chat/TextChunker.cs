using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Chat;

public record TextChunk(int PageNumber, int ChunkIndexOnPage, string Text);

/// <summary>Splits a McaFilingDocument's extracted text into page-level chunks by reusing the
/// "--- Page N (native|OCR) ---" markers PdfTextExtractor already writes — no change to Phase 2's
/// extraction needed. A chunk never spans pages, so its citation page number is always exact. An oversized
/// page is split at paragraph boundaries with a character overlap between adjacent sub-chunks (still on the
/// same page) so a clause like "Property Particulars: Survey No..." is never cut across a chunk boundary.</summary>
public static partial class TextChunker
{
    public static List<TextChunk> Chunk(string fullText, ChatIndexingOptions options)
    {
        var result = new List<TextChunk>();
        foreach (var (pageNumber, pageText) in SplitIntoPages(fullText))
        {
            var trimmed = pageText.Trim();
            if (trimmed.Length < options.ChunkMinChars)
                continue; // near-empty page — skip rather than embed noise

            if (trimmed.Length <= options.ChunkMaxChars)
            {
                result.Add(new TextChunk(pageNumber, 0, trimmed));
                continue;
            }

            var subChunks = SplitWithOverlap(trimmed, options.ChunkMaxChars, options.ChunkOverlapChars);
            for (var i = 0; i < subChunks.Count; i++)
                result.Add(new TextChunk(pageNumber, i, subChunks[i]));
        }
        return result;
    }

    private static List<(int PageNumber, string Text)> SplitIntoPages(string fullText)
    {
        var matches = PageMarkerRegex().Matches(fullText).Cast<Match>().ToList();
        if (matches.Count == 0)
            return [(1, fullText)]; // no markers found (unexpected format) — defensive fallback: whole text as one page

        var pages = new List<(int, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : fullText.Length;
            pages.Add((int.Parse(matches[i].Groups[1].Value), fullText[start..end]));
        }
        return pages;
    }

    private static List<string> SplitWithOverlap(string text, int maxChars, int overlapChars)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var para in paragraphs)
        {
            if (current.Length > 0 && current.Length + para.Length + 2 > maxChars)
            {
                chunks.Add(current.ToString());
                var currentText = current.ToString();
                var overlapText = currentText.Length > overlapChars ? currentText[^overlapChars..] : currentText;
                current.Clear();
                current.Append(overlapText);
            }

            if (para.Length > maxChars)
            {
                // A single paragraph too large on its own — hard-cut it with overlap, flushing whatever
                // was pending first.
                //
                // Regression note: the step size MUST be a fixed stride computed once (maxChars -
                // overlapChars), not "however much is left in this chunk minus overlapChars" — using the
                // shrinking remainder as the basis for the step meant that once the tail got shorter than
                // overlapChars, the step collapsed to Math.Max(1, negative) = 1, degenerating into a
                // single-character-advance loop that produced ~200 near-duplicate one-page chunks per
                // oversized page instead of ~2 (found live: a 422K-char, 166-page XBRL document with dense,
                // no-blank-line pages produced 27,914 chunks instead of a few hundred, and would have done
                // the same to the real Vertex AI embedding bill for every such document in the corpus).
                if (current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); }
                var stride = Math.Max(1, maxChars - overlapChars);
                var start = 0;
                while (start < para.Length)
                {
                    var len = Math.Min(maxChars, para.Length - start);
                    chunks.Add(para.Substring(start, len));
                    if (start + len >= para.Length)
                        break; // just emitted the final (possibly short) tail chunk
                    start += stride;
                }
                continue;
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(para);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    [GeneratedRegex(@"--- Page (\d+) \((native|OCR)\) ---")]
    private static partial Regex PageMarkerRegex();
}
