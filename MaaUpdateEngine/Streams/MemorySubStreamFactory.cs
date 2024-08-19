namespace MaaUpdateEngine.Streams;

internal class MemorySubStreamFactory : ISubStreamFactory
{
    private readonly ArraySegment<byte> memory;
    private readonly bool writable;

    public MemorySubStreamFactory(MemoryStream ms, bool writable)
    {
        if (!ms.TryGetBuffer(out memory))
        {
            throw new InvalidOperationException("MemoryStream must be backed by an array");
        }
        this.writable = writable;
    }

    public Stream CreateSubStream(long offset, long length)
    {
        return new MemoryStream(memory.Array!, memory.Offset + (int)offset, (int)length, writable, true);
    }
}