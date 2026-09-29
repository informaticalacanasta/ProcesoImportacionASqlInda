using System.Globalization;
using System.Text.RegularExpressions;

namespace DbInda.Worker.Orders;

public sealed class PedidoFileIdentity
{
    public required string LogicalName { get; init; }
    public required string Stamp { get; init; }
    public required int StoreId { get; init; }
    public required int CashRegisterId { get; init; }
    public DateTime? OrderedAt { get; init; }
}

public static partial class PedidoFileClassifier
{
    // 0 + yyMMdd + HHmmss. The batch prefix and an organizer _REPETIDO_ suffix are ignored.
    [GeneratedRegex(
        @"(?:^|_)(?<logical>PED_(?<stamp>0(?<yy>\d{2})(?<mo>\d{2})(?<dd>\d{2})(?<hh>\d{2})(?<mm>\d{2})(?<ss>\d{2}))_(?<tienda>\d{5})_(?<caja>\d{5})_PEDIDOS_TMPP)(?<extra>(?:_[A-Za-z0-9]+)*)\.TXT$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex OrderName();

    public static bool IsOrderFile(string pathOrName)
        => TryMatch(Path.GetFileName(pathOrName), out _);

    public static bool IsImportCandidate(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName);
        if (string.IsNullOrEmpty(name) || name[0] == '.')
            return false;
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Contains("__", StringComparison.Ordinal))
            return false;
        return TryMatch(name, out _);
    }

    public static bool TryMatch(string fileName, out PedidoFileIdentity identity)
    {
        identity = null!;
        var match = OrderName().Match(fileName);
        if (!match.Success)
            return false;

        var year = 2000 + int.Parse(match.Groups["yy"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["mo"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["dd"].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture);
        DateTime? orderedAt = null;
        try
        {
            orderedAt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            orderedAt = null;
        }

        identity = new PedidoFileIdentity
        {
            LogicalName = match.Groups["logical"].Value + Path.GetExtension(fileName),
            Stamp = match.Groups["stamp"].Value,
            StoreId = int.Parse(match.Groups["tienda"].Value, CultureInfo.InvariantCulture),
            CashRegisterId = int.Parse(match.Groups["caja"].Value, CultureInfo.InvariantCulture),
            OrderedAt = orderedAt
        };
        return true;
    }

    public static string WithHashSuffix(string logicalName, string sha256)
    {
        var stem = Path.GetFileNameWithoutExtension(logicalName);
        var extension = Path.GetExtension(logicalName);
        return stem + "_" + sha256[..8] + extension;
    }
}
