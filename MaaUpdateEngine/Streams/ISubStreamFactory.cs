namespace MaaUpdateEngine.Streams;

internal interface ISubStreamFactory
{
    public Stream CreateSubStream(long offset, long length);
}
