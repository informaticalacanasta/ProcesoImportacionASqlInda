using System.Net;
using System.Xml;

namespace DbInda.Worker.Validation;

/// <summary>
/// Rechaza cualquier resolución HTTP/externa. No descarga esquemas en runtime.
/// Solo permite ficheros locales bajo el directorio XSD configurado.
/// </summary>
internal sealed class BlockingXmlResolver : XmlResolver
{
    private readonly string _xsdDirectory;

    public BlockingXmlResolver(string xsdDirectory)
    {
        _xsdDirectory = Path.GetFullPath(xsdDirectory);
    }

    public override ICredentials? Credentials
    {
        set { }
    }

    public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
    {
        if (absoluteUri.IsFile)
        {
            var localPath = Path.GetFullPath(absoluteUri.LocalPath);
            if (!IsInsideDirectory(localPath, _xsdDirectory))
                throw new XmlException($"Resolución de esquema fuera del directorio XSD rechazada: {absoluteUri}");
            if (!File.Exists(localPath))
                throw new XmlException($"No se encontró el esquema local '{localPath}'.");
            return File.OpenRead(localPath);
        }

        throw new XmlException($"Resolución externa de esquema rechazada: {absoluteUri}");
    }

    private static bool IsInsideDirectory(string fullPath, string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(root, comparison);
    }
}
