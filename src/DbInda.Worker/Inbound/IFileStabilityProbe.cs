namespace DbInda.Worker.Inbound;

public interface IFileStabilityProbe
{
    bool Exists(string path);
    bool TryObserve(string path, out long length, out DateTime lastWriteUtc);
}
