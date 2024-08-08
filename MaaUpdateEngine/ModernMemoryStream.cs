using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    internal class ModernMemoryStream : Stream
    {
        private Memory<byte> memory;

        private long _position;

        public ModernMemoryStream(Memory<byte> memory)
        {
            this.memory = memory;
            CanWrite = true;
        }

        public ModernMemoryStream(ReadOnlyMemory<byte> memory)
        {
            this.memory = MemoryMarshal.AsMemory(memory);
            CanWrite = false;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite { get; }

        public override long Length => memory.Length;

        public override long Position
        {
            get => _position; set
            {
                if (value < 0 || value > memory.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(Position));
                }
                _position = value;
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
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            return Read(buffer.AsSpan(offset, count));
        }
        public override int Read(Span<byte> buffer)
        {
            int toRead = (int)Math.Min(buffer.Length, memory.Length - _position);
            memory.Slice((int)_position, toRead).Span.CopyTo(buffer);
            _position += toRead;
            return toRead;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin)
            {
                Position = offset;
            }
            else if (origin == SeekOrigin.Current)
            {
                Position += offset;
            }
            else if (origin == SeekOrigin.End)
            {
                Position = Length + offset;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(origin));
            }
            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!CanWrite)
            {
                throw new InvalidOperationException("stream is read only");
            }
            if (memory.Length - _position < buffer.Length)
            {
                throw new InvalidOperationException("Insufficient space in memory");
            }
            buffer.CopyTo(memory.Slice((int)_position).Span);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }
    }
}
