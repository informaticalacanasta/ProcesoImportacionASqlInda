using DbInda.Worker.Files;

namespace DbInda.Worker.Orders;

public static class PedidoFileArchive
{
    public static string Plan(string fileName, string directory, string hash)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination) || Same(destination, hash))
            return destination;

        var suffixed = Path.Combine(directory, PedidoFileClassifier.WithHashSuffix(fileName, hash));
        if (!File.Exists(suffixed) || Same(suffixed, hash))
            return suffixed;

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "_" + hash + Path.GetExtension(fileName));
    }

    public static string Place(string source, string plannedPath, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plannedPath)!);
        if (File.Exists(plannedPath))
        {
            if (!Same(plannedPath, hash))
                throw new IOException($"El destino del pedido está ocupado por otro contenido: {plannedPath}");
            File.Delete(source);
            return plannedPath;
        }

        File.Move(source, plannedPath, overwrite: false);
        return plannedPath;
    }

    private static bool Same(string path, string hash)
        => string.Equals(Sha256FileHasher.ComputeHex(path), hash, StringComparison.OrdinalIgnoreCase);
}
