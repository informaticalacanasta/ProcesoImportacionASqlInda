using DbInda.Worker.Configuration;

namespace DbInda.Worker.Orders;

public static class PedidoLayout
{
    public static string Inbox(PathsOptions paths, OrganizationOptions organization)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(organization.Inbox)
            ? Path.Combine(paths.Input, "inbox")
            : organization.Inbox);

    public static string Root(OrderOptions options)
    {
        var pending = Path.GetFullPath(options.Pending);
        return Path.GetDirectoryName(pending)
            ?? throw new InvalidOperationException("Orders:Pending no tiene carpeta padre para el espejo.");
    }

    public static string Mirror(OrderOptions options) => Path.Combine(Root(options), ".mirror");

    public static string Staging(OrderOptions options) => Path.Combine(Root(options), ".staging");
}
