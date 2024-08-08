namespace MaaUpdateEngine;

internal class HttpRandomAccessFileStream : Stream
{
    private readonly HttpRandomAccessFile httpFile;
    private long position;

    public HttpRandomAccessFileStream(HttpRandomAccessFile httpFile)
    {
        this.httpFile = httpFile;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => httpFile.Length;

    public override long Position
    {
        get => position;
        set {
            if (value < 0 || value > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Position is outside the bounds of the file.");
            }
            position = value;
        }
    }

    public override void Flush()
    {
        
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bufferSpan = buffer.AsSpan(offset, count);
        int bytesRead = httpFile.ReadAt(position, bufferSpan);
        position += bytesRead;
        return bytesRead;
    }

    public override int Read(Span<byte> buffer)
    {
        int bytesRead = httpFile.ReadAt(position, buffer);
        position += bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var memoryBuffer = new Memory<byte>(buffer, offset, count);
        int bytesRead = await httpFile.ReadAtAsync(position, memoryBuffer, cancellationToken);
        position += bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int bytesRead = await httpFile.ReadAtAsync(position, buffer, cancellationToken);
        position += bytesRead;
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin:
                Position = offset;
                break;
            case SeekOrigin.Current:
                Position += offset;
                break;
            case SeekOrigin.End:
                Position = Length + offset;
                break;
        }
        return position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}