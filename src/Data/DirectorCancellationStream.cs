namespace VSDirector.Data;

// Check cancellation at each serialization/decompression buffer boundary.
internal sealed class DirectorCancellationStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { cancellationToken.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer)
    { cancellationToken.ThrowIfCancellationRequested(); return inner.Read(buffer); }
    public override void Write(byte[] buffer, int offset, int count)
    { cancellationToken.ThrowIfCancellationRequested(); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { cancellationToken.ThrowIfCancellationRequested(); inner.Write(buffer); }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
