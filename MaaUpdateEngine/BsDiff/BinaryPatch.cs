using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Runtime.InteropServices;

namespace BsDiff;

/*
The original bsdiff.c source code (http://www.daemonology.net/bsdiff/) is
distributed under the following license:

Copyright 2003-2005 Colin Percival
All rights reserved

Redistribution and use in source and binary forms, with or without
modification, are permitted providing that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
	notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
	notice, this list of conditions and the following disclaimer in the
	documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
*/
public class BinaryPatch
{
	private const long c_fileSignature = 0x3034464649445342L;
	private const int c_headerSize = 32;

	private Memory<byte> m_controlBlock;
	private Memory<byte> m_diffBlock;
	private Memory<byte> m_extraBlock;
	private long m_newSize;

	private static byte[] ReadFully(Stream s, int bufferSize) {
		using var ms = new MemoryStream();
		var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
		try {
			int bytesRead;
			while ((bytesRead = s.Read(buffer, 0, buffer.Length)) > 0) {
				ms.Write(buffer, 0, bytesRead);
			}
			return ms.ToArray();
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
		}
	
	}
	public static BinaryPatch ReadFrom(Stream s)
	{
		Span<byte> header = stackalloc byte[c_headerSize];
		s.ReadExactly(header);

		byte control_compression, diff_compression, extra_compression;

		if (header.SequenceEqual("BSDIFF40"u8.Slice(0, 8)))
		{
			control_compression = (byte) CompressionMethod.Bzip2;
			diff_compression = (byte) CompressionMethod.Bzip2;
			extra_compression = (byte) CompressionMethod.Bzip2;
		}
		else if (header.Slice(0,5).SequenceEqual("BSDFM"u8.Slice(0 ,5)))
		{
			control_compression = header[5];
			diff_compression = header[6];
			extra_compression = header[7];
		}
		else
		{
			throw new InvalidOperationException("Corrupt patch.");
		}

		if (!Compressor.IsValidMethod(control_compression))
			throw new InvalidOperationException("Unsupported compression.");
		if (!Compressor.IsValidMethod(diff_compression))
			throw new InvalidOperationException("Unsupported compression.");
		if (!Compressor.IsValidMethod(extra_compression))
			throw new InvalidOperationException("Unsupported compression.");

		var controlLength = ReadInt64(header[8..]);
		var diffLength = ReadInt64(header[16..]);
		var newSize = ReadInt64(header[24..]);

		if (controlLength < 0 || diffLength < 0 || newSize < 0)
			throw new InvalidOperationException("Corrupt patch.");

		var controlBlock = new byte[controlLength];
		var diffBlock = new byte[diffLength];


		s.ReadExactly(controlBlock);
		s.ReadExactly(diffBlock);
		var extraBlock = ReadFully(s, 65536);

		var decompressed_control = Compressor.Decompress((CompressionMethod) control_compression, controlBlock);
		var decompressed_diff = Compressor.Decompress((CompressionMethod) diff_compression, diffBlock);
		var decompressed_extra = Compressor.Decompress((CompressionMethod) extra_compression, extraBlock);

		return new BinaryPatch
		{
			m_controlBlock = decompressed_control,
			m_diffBlock = decompressed_diff,
			m_extraBlock = decompressed_extra,
			m_newSize = newSize
		};
	}

	public void Apply(Stream input, Stream output)
	{
		// check arguments
#if NET6_0_OR_GREATER
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);
#else
		if (input is null)
			throw new ArgumentNullException(nameof(input));
		if (diff is null)
			throw new ArgumentNullException(nameof(diff));
		if (output is null)
			throw new ArgumentNullException(nameof(output));
#endif

		/*
		File format:
			0	8	"BSDIFF40"
			8	8	X
			16	8	Y
			24	8	sizeof(newfile)
			32	X	bzip2(control block)
			32+X	Y	bzip2(diff block)
			32+X+Y	???	bzip2(extra block)
		with control block a set of triples (x,y,z) meaning "add x bytes
		from oldfile to x bytes from the diff block; copy y bytes from the
		extra block; seek forwards in oldfile by z bytes".
		*/
		// read header

		// preallocate buffers for reading and writing
		const int c_bufferSize = 1048576;
		var newData = GC.AllocateArray<byte>(c_bufferSize, true);
		var oldData = GC.AllocateArray<byte>(c_bufferSize, true);

		// decompress each part (to read it)

		static MemoryStream GetMemoryStream(ReadOnlyMemory<byte> memory)
		{
			if (memory.IsEmpty)
				return new MemoryStream(Array.Empty<byte>(), false);
			if (MemoryMarshal.TryGetArray(memory, out var array))
				return new MemoryStream(array.Array!, array.Offset, array.Count);
			return new MemoryStream(memory.ToArray(), false);
		}

		using var controlStream = GetMemoryStream(m_controlBlock);
		using var diffStream = GetMemoryStream(m_diffBlock);
		using var extraStream = GetMemoryStream(m_extraBlock);
		Span<byte> buffer = stackalloc byte[24];
		long addBytes, copyBytes, seekBytes;

		long oldFilePosition = 0;
		long newFilePosition = 0;
		while (newFilePosition < m_newSize)
		{
			controlStream.ReadExactly(buffer);
			addBytes = ReadInt64(buffer);
			copyBytes = ReadInt64(buffer[8..]);
			seekBytes = ReadInt64(buffer[16..]);

			// sanity-check
			if (newFilePosition + addBytes > m_newSize)
				throw new InvalidOperationException("Corrupt patch.");

			// seek old file to the position that the new data is diffed against
			input.Position = oldFilePosition;

			var bytesToCopy = (int) addBytes;
			while (bytesToCopy > 0)
			{
				var actualBytesToCopy = Math.Min(bytesToCopy, c_bufferSize);

				// read diff string
				diffStream.ReadExactly(newData, 0, actualBytesToCopy);

				// add old data to diff string
				input.ReadExactly(oldData, 0, actualBytesToCopy);

				for (var index = 0; index < actualBytesToCopy; index++)
					newData[index] += oldData[index];

				output.Write(newData, 0, actualBytesToCopy);

				// adjust counters
				newFilePosition += actualBytesToCopy;
				oldFilePosition += actualBytesToCopy;
				bytesToCopy -= actualBytesToCopy;
			}

			// sanity-check
			if (newFilePosition + copyBytes > m_newSize)
				throw new InvalidOperationException("Corrupt patch.");

			// read extra string
			bytesToCopy = (int) copyBytes;
			while (bytesToCopy > 0)
			{
				var actualBytesToCopy = Math.Min(bytesToCopy, c_bufferSize);

				extraStream.ReadExactly(newData, 0, actualBytesToCopy);
				output.Write(newData, 0, actualBytesToCopy);

				newFilePosition += actualBytesToCopy;
				bytesToCopy -= actualBytesToCopy;
			}

			// adjust position
			oldFilePosition += seekBytes;
		}
	}


	// Reads a long value stored in sign/magnitude format.
	internal static long ReadInt64(ReadOnlySpan<byte> buffer)
	{
		var value = BinaryPrimitives.ReadInt64LittleEndian(buffer);
		var mask = value >> 63;
		return (~mask & value) | (((value & unchecked((long) 0x8000_0000_0000_0000)) - value) & mask);
	}

	// Writes a long value in sign/magnitude format.
	internal static void WriteInt64(Span<byte> buffer, long value)
	{
		var mask = value >> 63;
		BinaryPrimitives.WriteInt64LittleEndian(buffer, ((value + mask) ^ mask) | (value & unchecked((long) 0x8000_0000_0000_0000)));
	}

}
