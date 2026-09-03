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
            if (!localPath.StartsWith(_xsdDirectory, StringComparison.OrdinalIgnoreCase))
                throw new XmlException($"Resolución de esquema fuera del directorio XSD rechazada: {absoluteUri}");
            if (!File.Exists(localPath))
                throw new XmlException($"No se encontró el esquema local '{localPath}'.");
            return File.OpenRead(localPath);
        }

        throw new XmlException($"Resolución externa de esquema rechazada: {absoluteUri}");
    }
}
