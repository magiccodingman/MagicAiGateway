using System.Buffers;
using System.Text;

namespace SharedMagic.Proxy;

public sealed class ToolCallObservingStream : Stream
{
    private readonly Stream _inner;
    private readonly IMagicToolRegistry _toolRegistry;
    private readonly Action<ModelResponseObservation> _onCompleted;
    private readonly StreamingToolCallAccumulator _streaming = new();
    private readonly MemoryStream _body = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly int _maximumBufferedBytes;
    private int _completed;

    public ToolCallObservingStream(
        Stream inner,
        IMagicToolRegistry toolRegistry,
        Action<ModelResponseObservation> onCompleted,
        int maximumBufferedBytes = 2 * 1024 * 1024)
    {
        _inner = inner;
        _toolRegistry = toolRegistry;
        _onCompleted = onCompleted;
        _maximumBufferedBytes = maximumBufferedBytes;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        FlushTextDecoder();
        if (_body.Length > 0)
        {
            _streaming.ObserveJson(_body.GetBuffer().AsSpan(0, checked((int)_body.Length)));
        }

        var calls = _streaming.GetToolCalls();
        _onCompleted(new(
            calls.Count > 0,
            calls.Any(x => _toolRegistry.Contains(x.Name)),
            calls));

        return ValueTask.CompletedTask;
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_body.Length + bytes.Length <= _maximumBufferedBytes)
        {
            _body.Write(bytes);
        }

        var charCount = _decoder.GetCharCount(bytes, flush: false);
        if (charCount == 0)
        {
            return;
        }

        var chars = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            var written = _decoder.GetChars(bytes, chars.AsSpan(0, charCount), flush: false);
            ObserveText(chars.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private void ObserveText(ReadOnlySpan<char> chars)
    {
        foreach (var character in chars)
        {
            if (character == '\n')
            {
                ProcessLine(_lineBuffer.ToString().TrimEnd('\r'));
                _lineBuffer.Clear();
            }
            else
            {
                _lineBuffer.Append(character);
            }
        }
    }

    private void FlushTextDecoder()
    {
        Span<char> chars = stackalloc char[8];
        var written = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        ObserveText(chars[..written]);
        if (_lineBuffer.Length > 0)
        {
            ProcessLine(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }
    }

    private void ProcessLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = line[5..].Trim();
        if (payload.Length == 0 || payload == "[DONE]")
        {
            return;
        }

        _streaming.ObserveJson(Encoding.UTF8.GetBytes(payload));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Observe(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Observe(buffer.AsSpan(offset, count));
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Observe(buffer.Span);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
