namespace DbInda.Worker.Inbound;

public static class FilePathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    public static bool HasXmlExtension(string path)
        => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase);
}
