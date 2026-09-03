using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public sealed class TicketDocumentReader
{
    private readonly TicketXmlParser _xmlParser = new();
    private readonly TicketFileNameParser _fileNameParser = new();

    public ParseResult Read(string xml, string fileName)
    {
        var xmlResult = _xmlParser.Parse(xml);
        var parsedName = _fileNameParser.Parse(fileName);
        var warnings = new List<ConversionWarning>();
        warnings.AddRange(parsedName.Warnings);
        warnings.AddRange(xmlResult.Warnings);

        if (xmlResult.Success && xmlResult.Ticket is not null)
            warnings.AddRange(FileNameXmlComparer.Compare(parsedName, xmlResult.Ticket));

        return new ParseResult
        {
            Success = xmlResult.Success,
            Ticket = xmlResult.Ticket,
            FileName = parsedName,
            Warnings = warnings,
            Errors = xmlResult.Errors,
            UnknownElements = xmlResult.UnknownElements
        };
    }
}
