using DbInda.Worker.Models;

namespace DbInda.Worker.Processing;

public static class TicketQualityEvaluator
{
    public static string Evaluate(ParsedTicket? ticket, IReadOnlyList<ConversionWarning> persistenceWarnings)
    {
        if (ticket is null
            || ticket.NifEmisor is null
            || ticket.NumFactura is null
            || ticket.FechaExpedicion is null)
        {
            return TicketQualityStatuses.Incompleto;
        }

        if (persistenceWarnings.Count > 0)
            return TicketQualityStatuses.ConAdvertencias;

        return TicketQualityStatuses.Ok;
    }
}
