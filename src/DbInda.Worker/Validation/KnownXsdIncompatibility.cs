using System.Xml.Schema;
using DbInda.Worker.Models;

namespace DbInda.Worker.Validation;

public static class KnownXsdIncompatibility
{
    public const string XmlDsigOutOfScopeCode = "XMLDSIG_FUERA_DE_ALCANCE";
    public const string XmlDsigSignatureNotValidatedCode = "XMLDSIG_SIGNATURE_NO_VALIDADA";

    public const string XmlDsigOutOfScopeMessage =
        "XMLDSig queda fuera del alcance de esta validación. El import oficial no se carga ni se descarga. No se valida la estructura ni la criptografía de ds:Signature.";

    public const string XmlDsigSignatureNotValidatedMessage =
        "Se detectó ds:Signature en el XML. Este importer no valida su estructura XSD ni su firma criptográfica. El documento se conserva para revisión.";

    public static bool IsKnown(ValidationEventArgs args)
        => IsKnown(args.Message ?? "");

    public static bool IsKnown(string message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        if (Contains(message, XmlDsigOutOfScopeCode) || Contains(message, XmlDsigSignatureNotValidatedCode))
            return true;

        if (Contains(message, "PvpConsumo") || Contains(message, "PVPConsumo"))
            return true;

        if (Contains(message, "schemaLocation"))
            return true;

        if (Contains(message, "xmldsig"))
            return true;

        if (Contains(message, "Signature"))
            return true;

        return false;
    }

    private static bool Contains(string message, string token)
        => message.Contains(token, StringComparison.OrdinalIgnoreCase);
}
