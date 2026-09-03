namespace DbInda.Worker.Files;

public enum ArchiveKind
{
    Processed,
    Error
}

public sealed class ArchiveRequest
{
    public required string SourcePath { get; init; }
    public required string HashSha256 { get; init; }
    public required ArchiveKind Kind { get; init; }
    public required DateOnly FolderDate { get; init; }
    public int? Tienda { get; init; }
    public required long ReceptionId { get; init; }
}

public sealed class ArchiveResult
{
    public required string FinalPath { get; init; }
    public bool CollisionAvoided { get; init; }
}

public interface IXmlFileArchiver
{
    string AllocateDestination(ArchiveRequest request);
    Task MoveToExactAsync(string sourcePath, string destinationPath, string expectedHash, CancellationToken cancellationToken);
    bool Exists(string path);
    string? TryComputeHash(string path);
}
