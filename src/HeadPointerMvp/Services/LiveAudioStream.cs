using System.Collections.Concurrent;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

/// <summary>
/// Bridges a push-style audio producer (our resampling pump loop, writing chunks as they arrive)
/// into the pull-style <see cref="Stream"/> that <c>SpeechRecognitionEngine.SetInputToAudioStream</c>
/// reads from on its own internal thread -- mimicking how a live microphone read behaves (Read blocks
/// until data is available) so SAPI treats this exactly like SetInputToDefaultAudioDevice() would.
/// Bounded so a consumer that falls behind can't grow this without limit.
/// </summary>
public sealed class LiveAudioStream(FileLogger? logger = null) : Stream
{
    private const int BoundedCapacity = 500; // ~a few seconds of chunks at typical WASAPI callback sizes
    private readonly BlockingCollection<byte[]> _chunks = new(BoundedCapacity);
    private byte[]? _pending;
    private int _pendingOffset;
    private long _readCalls;

    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    // SAPI's internal audio setup probes these even for a stream that reports CanSeek=false, so
    // throwing NotSupportedException here (the technically "correct" Stream contract for an infinite
    // live feed) broke SetInputToAudioStream entirely. But reporting the *actual* running byte count
    // for Length was also wrong: at setup time Position is still 0, so Length also read 0, which reads
    // as "empty stream" -- SAPI made exactly one zero-byte probe Read() and then never read again,
    // even once real audio was flowing. Reporting a large fixed value keeps Length always well ahead
    // of Position, so nothing ever looks exhausted.
    public override long Length => long.MaxValue / 2;
    public override long Position { get => _position; set { } }

    /// <summary>Enqueues a chunk for the reader; drops it if the reader has fallen behind and the bounded buffer is full, rather than blocking the producer indefinitely (which would silently freeze the whole capture pipeline with no exception and nothing to log).</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count == 0) return;
        var chunk = new byte[count];
        Array.Copy(buffer, offset, chunk, 0, count);
        try
        {
            _chunks.TryAdd(chunk, millisecondsTimeout: 200);
        }
        catch (InvalidOperationException)
        {
            // Complete() was called concurrently with a producer still writing during shutdown.
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _readCalls++;
        if (_readCalls == 1) _ = logger?.InfoAsync($"[voice-diag] SAPI's first Read() call: requested {count} bytes.");
        else if (_readCalls % 100 == 0) _ = logger?.InfoAsync($"[voice-diag] SAPI Read() calls={_readCalls}, running position={_position}.");

        var written = 0;
        while (written < count)
        {
            if (_pending is null)
            {
                if (!_chunks.TryTake(out _pending, Timeout.Infinite)) break; // Complete() was called and the buffer drained
                _pendingOffset = 0;
            }

            var available = _pending.Length - _pendingOffset;
            var toCopy = Math.Min(available, count - written);
            Array.Copy(_pending, _pendingOffset, buffer, offset + written, toCopy);
            _pendingOffset += toCopy;
            written += toCopy;

            if (_pendingOffset >= _pending.Length) _pending = null;
        }

        _position += written;
        return written;
    }

    /// <summary>Signals no more data is coming, so a blocked Read() returns rather than hanging forever.</summary>
    public void Complete()
    {
        try
        {
            _chunks.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed -- fine, that implies Complete() already happened via Dispose().
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _position;
    public override void SetLength(long value) { }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete();
            _chunks.Dispose();
        }

        base.Dispose(disposing);
    }
}
