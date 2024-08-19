namespace MaaUpdateEngine.Streams;

internal class RandomAccessSubStreamFactory(FileStream fs, bool writable, bool leaveOpen) : ISubStreamFactory, IDisposable
{
    private bool disposed;
    public Stream CreateSubStream(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new RandomAccessSubStream(fs.SafeFileHandle, offset, length, writable);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (!leaveOpen)
        {
            fs.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    ~RandomAccessSubStreamFactory()
    {
        Dispose();
    }
}
