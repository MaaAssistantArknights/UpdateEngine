
namespace MaaUpdateEngine;

internal interface ISafeFileOperation
{
    public void Rename(string source, string destination);
    public void Unlink(string path);
}

internal static class SafeFileOperation
{
    private static readonly ISafeFileOperation instance = GetImplementation();

    private static ISafeFileOperation GetImplementation()
    {
       return SafeFileOperationWin32.IsSupported ? new SafeFileOperationWin32() : new SafeFileOperationDefault();
    }

    public static void Rename(string source, string destination)
    {
        instance.Rename(source, destination);
    }

    public static void Unlink(string path)
    {
        instance.Unlink(path);
    }
}

internal class SafeFileOperationDefault : ISafeFileOperation
{
    public void Rename(string source, string destination)
    {
        try
        {
            File.Move(source, destination, true);
        }
        catch (UnauthorizedAccessException e) { throw new IOException("Rename failed", e); }
    }

    public void Unlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException e) { throw new IOException("Unlink failed", e); }
    }
}

