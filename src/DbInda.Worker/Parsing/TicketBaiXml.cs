using System.Xml.Linq;

namespace DbInda.Worker.Parsing;

internal static class TicketBaiXml
{
    public const string NamespaceUri = "urn:ticketbai:emision";

    public static XElement? Child(this XElement parent, string localName)
    {
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == localName)
                return child;
        }

        return null;
    }

    public static IEnumerable<XElement> Children(this XElement parent, string localName)
    {
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == localName)
                yield return child;
        }
    }

    public static string? ChildText(this XElement parent, string localName)
        => parent.Child(localName)?.Value;
}
