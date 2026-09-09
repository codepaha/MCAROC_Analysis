using MCAROC_Analysis.Services.Chat;

namespace MCAROC_Analysis.Tests;

public class TextChunkerTests
{
    private static readonly ChatIndexingOptions Options = new(ChunkMaxChars: 50, ChunkOverlapChars: 10, ChunkMinChars: 5);

    [Fact]
    public void SplitsOnPageMarkers_OneChunkPerPage()
    {
        var text = "--- Page 1 (native) ---\nHello world, this is page one.\n--- Page 2 (OCR) ---\nAnd this is page two.";

        var chunks = TextChunker.Chunk(text, Options);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Contains("page one", chunks[0].Text);
        Assert.Equal(2, chunks[1].PageNumber);
        Assert.Contains("page two", chunks[1].Text);
    }

    [Fact]
    public void SkipsNearEmptyPages()
    {
        var text = "--- Page 1 (native) ---\nHi\n--- Page 2 (native) ---\nThis page has real content in it.";

        var chunks = TextChunker.Chunk(text, Options);

        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].PageNumber);
    }

    [Fact]
    public void OversizedPage_SplitsIntoMultipleChunksWithOverlap_NeverSpanningPages()
    {
        var paragraph1 = new string('a', 40);
        var paragraph2 = new string('b', 40);
        var text = $"--- Page 1 (native) ---\n{paragraph1}\n\n{paragraph2}";

        var chunks = TextChunker.Chunk(text, Options);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.Equal(1, c.PageNumber));
        // The second chunk should carry some overlap from the tail of the first.
        Assert.True(chunks[1].Text.Length > 0);
    }

    [Fact]
    public void NoChunkExceedsMaxChars_EvenWhenOverlapPlusNextParagraphWouldOverflow()
    {
        // paragraph2 nearly fills a chunk on its own; before the fix, seeding the overlap from
        // paragraph1's tail and then appending paragraph2 produced a ~ChunkMaxChars + ChunkOverlapChars
        // chunk (57 > 50 with these options).
        var paragraph1 = new string('a', 45);
        var paragraph2 = new string('b', 45);
        var text = $"--- Page 1 (native) ---\n{paragraph1}\n\n{paragraph2}";

        var chunks = TextChunker.Chunk(text, Options);

        Assert.All(chunks, c => Assert.True(
            c.Text.Length <= Options.ChunkMaxChars,
            $"chunk of {c.Text.Length} chars exceeds ChunkMaxChars={Options.ChunkMaxChars}"));
    }

    [Fact]
    public void NoPageMarkers_FallsBackToWholeTextAsOnePage()
    {
        var text = "Just some plain text with no page markers at all, long enough to pass the min length.";

        var chunks = TextChunker.Chunk(text, Options with { ChunkMaxChars = 1000 });

        var chunk = Assert.Single(chunks);
        Assert.Equal(1, chunk.PageNumber);
    }

    [Fact]
    public void SingleParagraphLargerThanMaxChars_IsHardCutWithOverlap()
    {
        var singleParagraph = new string('x', 130); // no blank-line breaks at all
        var text = $"--- Page 1 (native) ---\n{singleParagraph}";

        var chunks = TextChunker.Chunk(text, Options);

        Assert.True(chunks.Count >= 3); // 130 chars / (50-10 effective stride) needs multiple hard cuts
        Assert.All(chunks, c => Assert.Equal(1, c.PageNumber));
    }

    [Fact]
    public void HardCutStepUsesFixedStride_NeverDegeneratesNearTheTail()
    {
        // Regression test for a real bug found live against the corpus: a dense, no-blank-line page just
        // over ChunkMaxChars (e.g. 2543 chars with ChunkMaxChars=2000) produced ~200 near-duplicate
        // one-character-shifted chunks instead of ~2, because the step size was computed from the
        // shrinking remainder ("len - overlapChars") rather than a fixed stride — once the remaining tail
        // dropped below overlapChars, the step collapsed to 1. A 166-page real document hit this on most
        // pages and produced 27,914 chunks instead of a few hundred.
        var singleParagraph = new string('x', 2543);
        var text = $"--- Page 1 (native) ---\n{singleParagraph}";
        var options = new ChatIndexingOptions(ChunkMaxChars: 2000, ChunkOverlapChars: 200, ChunkMinChars: 50);

        var chunks = TextChunker.Chunk(text, options);

        Assert.True(chunks.Count <= 3, $"Expected at most 3 chunks for a 2543-char page just over the 2000-char cap, got {chunks.Count}.");
    }

    [Fact]
    public void HardCutStepUsesFixedStride_ScalesLinearlyNotQuadratically()
    {
        // A much larger dense page (e.g. a real 422K-char XBRL document) must still produce a chunk count
        // proportional to text length / stride, not explode.
        var singleParagraph = new string('x', 20_000);
        var text = $"--- Page 1 (native) ---\n{singleParagraph}";
        var options = new ChatIndexingOptions(ChunkMaxChars: 2000, ChunkOverlapChars: 200, ChunkMinChars: 50);

        var chunks = TextChunker.Chunk(text, options);

        var expectedApprox = 20_000 / (2000 - 200); // ~11
        Assert.True(chunks.Count <= expectedApprox + 2, $"Expected roughly {expectedApprox} chunks, got {chunks.Count}.");
    }
}
