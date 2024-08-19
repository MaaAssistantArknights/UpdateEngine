using Microsoft.Win32.SafeHandles;

namespace MaaUpdateEngine.Streams;

internal class RandomAccessSubStream : Stream
{
    private SafeFileHandle _fd;
    private long _fd_offset;
    private long _length;

    private long _position;

    public RandomAccessSubStream(SafeFileHandle fd, long offset, long length, bool canWrite)
    {
        _fd = fd;
        _fd_offset = offset;
        _length = length;
        CanWrite = canWrite;
    }
    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite { get; }

    public override long Length => _length;

    public override long Position
    {
        get => _position; set
        {
            if (value < 0 || value > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Position is outside the bounds of the file.");
            }
            _position = value;
        }
    }

    public override void Flush()
    {
        if (CanWrite)
        {
            RandomAccess.FlushToDisk(_fd);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var available = Length - Position;
        if (available == 0)
        {
            return 0;
        }
        buffer = buffer.Slice(0, (int)Math.Min(buffer.Length, available));
        var len = RandomAccess.Read(_fd, buffer, _fd_offset + _position);
        _position += len;
        return len;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var available = Length - Position;
        if (available == 0)
        {
            return 0;
        }
        buffer = buffer.Slice(0, (int)Math.Min(buffer.Length, available));
        var len = await RandomAccess.ReadAsync(_fd, buffer, _fd_offset + _position, cancellationToken);
        _position += len;
        return len;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return origin switch
        {
            SeekOrigin.Begin => Position = offset,
            SeekOrigin.Current => Position += offset,
            SeekOrigin.End => Position = Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite)
        {
            throw new NotSupportedException();
        }
        if (_position + buffer.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), "Write would exceed the bounds of the file.");
        }
        RandomAccess.Write(_fd, buffer, _fd_offset + _position);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanWrite)
        {
            throw new NotSupportedException();
        }
        if (_position + buffer.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), "Write would exceed the bounds of the file.");
        }

        await RandomAccess.WriteAsync(_fd, buffer, _fd_offset + _position, cancellationToken);
        _position += buffer.Length;
    }
}
