using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DbInda.Worker.Models;

namespace DbInda.Worker.Validation;

/// <summary>
/// Valida contra tiquets.xsd. XMLDSig se excluye deliberadamente en memoria:
/// no hay copia local del XSD oficial, no hay stub y no se descarga nada.
/// tiquets.xsd en disco no se modifica. Puede no ser UTF-8 real; se reinterpreta en memoria.
/// </summary>
public sealed class TicketXsdValidator
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private const string XmlDsigNamespace = "http://www.w3.org/2000/09/xmldsig#";

    private readonly XmlSchemaSet? _schemas;
    private readonly string? _loadError;

    public TicketXsdValidator(string xsdDirectory, string schemaFileName)
    {
        var schemaPath = Path.Combine(xsdDirectory, schemaFileName);
        if (!File.Exists(schemaPath))
        {
            _loadError = $"No se encontró el XSD '{schemaPath}'.";
            return;
        }

        try
        {
            var resolver = new BlockingXmlResolver(xsdDirectory);
            var schemas = new XmlSchemaSet { XmlResolver = resolver };

            var schemaXml = ReadSchemaAsUtf8Text(schemaPath);
            schemaXml = ExcludeXmlDsigDependency(schemaXml);

            using var reader = XmlReader.Create(new StringReader(schemaXml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = resolver
            });
            schemas.Add(null, reader);
            schemas.Compile();
            _schemas = schemas;
        }
        catch (Exception ex)
        {
            _loadError = $"No se pudo cargar el XSD: {ex.Message}";
        }
    }

    public XsdValidationResult Validate(string xml)
    {
        if (_schemas is null)
        {
            return new XsdValidationResult
            {
                XsdValido = null,
                EstadoValidacionXsd = XsdValidationStatuses.NoValidable,
                Events =
                [
                    new XsdValidationEvent
                    {
                        Severity = "Error",
                        Message = _loadError ?? "El esquema XSD no está disponible.",
                        IsKnownIncompatibility = false
                    }
                ]
            };
        }

        var events = new List<XsdValidationEvent>
        {
            new()
            {
                Severity = "Warning",
                Message = KnownXsdIncompatibility.XmlDsigOutOfScopeMessage,
                IsKnownIncompatibility = true
            }
        };

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = _schemas,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        settings.ValidationEventHandler += (_, args) =>
        {
            events.Add(new XsdValidationEvent
            {
                Severity = args.Severity.ToString(),
                Message = args.Message,
                IsKnownIncompatibility = KnownXsdIncompatibility.IsKnown(args)
            });
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            return new XsdValidationResult
            {
                XsdValido = null,
                EstadoValidacionXsd = XsdValidationStatuses.NoValidable,
                Events =
                [
                    new XsdValidationEvent
                    {
                        Severity = "Error",
                        Message = ex.Message,
                        IsKnownIncompatibility = false
                    }
                ]
            };
        }

        if (ContainsSignature(xml))
        {
            events.Add(new XsdValidationEvent
            {
                Severity = "Warning",
                Message = KnownXsdIncompatibility.XmlDsigSignatureNotValidatedMessage,
                IsKnownIncompatibility = true
            });
        }

        var hasUnknown = events.Any(e => !e.IsKnownIncompatibility);
        return new XsdValidationResult
        {
            XsdValido = false,
            EstadoValidacionXsd = hasUnknown
                ? XsdValidationStatuses.InvalidoDatos
                : XsdValidationStatuses.InvalidoIncompatibilidadConocida,
            Events = events
        };
    }

    internal static string ExcludeXmlDsigDependency(string schemaXml)
    {
        var document = XDocument.Parse(schemaXml);
        foreach (var import in document.Descendants(Xs + "import").ToList())
        {
            var ns = (string?)import.Attribute("namespace");
            var location = (string?)import.Attribute("schemaLocation");
            if (string.Equals(ns, XmlDsigNamespace, StringComparison.OrdinalIgnoreCase)
                || (location is not null && location.Contains("xmldsig", StringComparison.OrdinalIgnoreCase)))
            {
                import.Remove();
            }
        }

        foreach (var element in document.Descendants(Xs + "element").ToList())
        {
            var refName = (string?)element.Attribute("ref");
            if (refName is null)
                continue;
            if (refName.Equals("ds:Signature", StringComparison.OrdinalIgnoreCase)
                || (refName.Contains("Signature", StringComparison.OrdinalIgnoreCase)
                    && refName.Contains("ds:", StringComparison.OrdinalIgnoreCase)))
            {
                element.Remove();
            }
        }

        var dsDeclaration = document.Root?.Attributes()
            .FirstOrDefault(a => a.IsNamespaceDeclaration && a.Value == XmlDsigNamespace);
        dsDeclaration?.Remove();

        return document.ToString();
    }

    private static bool ContainsSignature(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            return document.Descendants().Any(e => e.Name.LocalName == "Signature");
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string ReadSchemaAsUtf8Text(string schemaPath)
    {
        var bytes = File.ReadAllBytes(schemaPath);
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
