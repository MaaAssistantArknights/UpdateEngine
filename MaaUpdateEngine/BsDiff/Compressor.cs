using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace BsDiff;

public enum CompressionMethod : byte
{
    Unknown = 0,
    None = (byte) '-',
    Deflate = (byte) 'D',
    Bzip2 = (byte) 'B',
    Zstd = (byte) 'Z'
}

internal static class Compressor
{


    public static bool IsValidMethod(byte method) => method switch
    {
        (byte) CompressionMethod.None => true,
        (byte) CompressionMethod.Deflate => true,
        (byte) CompressionMethod.Bzip2 => true,
        (byte) CompressionMethod.Zstd => true,
        _ => false
    };

    public static Memory<byte> Decompress(CompressionMethod method, ReadOnlySpan<byte> data)
    {
        return method switch
        {
            CompressionMethod.None => data.ToArray(),
            CompressionMethod.Deflate => Inflate(data),
            CompressionMethod.Bzip2 => Bzip2Decompress(data),
            CompressionMethod.Zstd => ZstdDecompress(data),
            _ => throw new NotSupportedException("Unsupported compression method")
        };
    }

    public static Memory<byte> Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.SmallestSize, true))
        {
            ds.Write(data);
        }
        return ms.GetBuffer().AsMemory(0, (int) ms.Length);
    }

    public static Memory<byte> Inflate(ReadOnlySpan<byte> data)
    {
        using var inms = new MemoryStream(data.ToArray());
        var outms = new MemoryStream();
        using var ds = new ZLibStream(inms, CompressionMode.Decompress, true);
        ds.CopyTo(outms);
        return outms.GetBuffer().AsMemory(0, (int)outms.Length);
    }

    public static Memory<byte> Bzip2(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var ds = new BZip2OutputStream(ms, 9) { IsStreamOwner = false })
        {
            ds.Write(data);
        }
        return ms.GetBuffer().AsMemory(0, (int) ms.Length);
    }

    public static Memory<byte> Bzip2Decompress(ReadOnlySpan<byte> data)
    {
        using var inStrm = new MemoryStream(data.ToArray());
        using var ds = new BZip2InputStream(inStrm);
        using var outStrm = new MemoryStream();
        ds.CopyTo(outStrm, 65536);
        return outStrm.GetBuffer().AsMemory(0, (int) outStrm.Length);
    }

    public static Memory<byte> ZstdCompress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using var comp = new ZstdSharp.Compressor(22);
        var buffer = new byte[ZstdSharp.Compressor.GetCompressBound(data.Length)];
        var len = comp.Wrap(data, buffer);
        return buffer.AsMemory(0, len);
    }

    public static Memory<byte> ZstdDecompress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var comp = new ZstdSharp.Decompressor();
        var buffer = new byte[ZstdSharp.Decompressor.GetDecompressedSize(data)];
        var len = comp.Unwrap(data, buffer);
        return buffer.AsMemory(0, len);
    }
}
