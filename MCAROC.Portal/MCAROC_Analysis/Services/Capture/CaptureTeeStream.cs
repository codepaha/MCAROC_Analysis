namespace MCAROC_Analysis.Services.Capture;

/// <summary>Hands a reply's bytes to the caller exactly as the inner stream yields them (errors and cancellation included) while keeping a copy of up
/// to <paramref name="limit"/> bytes for the capture. <paramref name="onDone"/> runs once: with the copy and <c>complete: true</c> when the caller reads
/// to the end, with a partial copy and <c>complete: false</c> when it disposes earlier, and with <c>null</c> when the reply outgrew the limit. It
/// never throws into the caller (<see cref="CaptureHandler"/> catches inside it).</summary>
public sealed class CaptureTeeStream(Stream inner, IDisposable owner, long limit, Action<byte[]?, bool> onDone) : Stream
{
    private readonly MemoryStream _copy = new();
    private bool _overLimit;
    private int _done;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Keep(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

    public override int Read(Span<byte> buffer) => Keep(buffer[..inner.Read(buffer)]);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        return Keep(buffer.Span[..n]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Keep(ReadOnlySpan<byte> read)
    {
        if (read.Length == 0)
        {
            Done(complete: true);
            return 0;
        }
        if (!_overLimit)
        {
            if (_copy.Length + read.Length > limit) { _overLimit = true; _copy.SetLength(0); }
            else _copy.Write(read);
        }
        return read.Length;
    }

    private void Done(bool complete)
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        try { onDone(_overLimit ? null : _copy.ToArray(), complete); } catch { /* a capture never fails the caller */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Done(complete: false);
            inner.Dispose();
            owner.Dispose();
            _copy.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
