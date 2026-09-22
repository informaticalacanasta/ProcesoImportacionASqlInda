namespace DbInda.Worker.Inbound;

public readonly record struct FileStabilityObservation(long Length, DateTime LastWriteTimeUtc);

public interface IFileStabilityProbe
{
    bool Exists(string path);
    bool TryObserve(string path, out long length, out DateTime lastWriteUtc);
}
