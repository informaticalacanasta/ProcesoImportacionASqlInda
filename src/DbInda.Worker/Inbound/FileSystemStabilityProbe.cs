namespace DbInda.Worker.Inbound;

public sealed class FileSystemStabilityProbe : IFileStabilityProbe
{
    public bool Exists(string path) => File.Exists(path);

    public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
    {
        length = 0;
        lastWriteUtc = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.None);

            info.Refresh();
            length = stream.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
