using DbInda.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Files;

public sealed class XmlFileArchiver : IXmlFileArchiver
{
    public const string SinTiendaFolder = "SIN_TIENDA";

    private readonly PathsOptions _paths;
    private readonly ILogger<XmlFileArchiver> _logger;

    public XmlFileArchiver(IOptions<PathsOptions> paths, ILogger<XmlFileArchiver> logger)
    {
        _paths = paths.Value;
        _logger = logger;
    }

    public string AllocateDestination(ArchiveRequest request)
    {
        var destDir = DestinationDirectory(request);
        Directory.CreateDirectory(destDir);

        var originalName = Path.GetFileName(request.SourcePath);
        if (string.IsNullOrWhiteSpace(originalName))
            originalName = request.ReceptionId + ".xml";

        var collision = false;
        foreach (var candidate in Candidates(destDir, originalName, request.ReceptionId))
        {
            if (!File.Exists(candidate))
            {
                if (collision)
                    _logger.LogInformation("Destino collision-safe para recepción {ReceptionId}: {Path}", request.ReceptionId, candidate);
                return candidate;
            }

            collision = true;
            _logger.LogInformation(
                "Nombre destino ocupado (no se reutiliza aunque el hash coincida): {Path}",
                candidate);
        }

        throw new IOException($"No se pudo asignar un destino libre para la recepción {request.ReceptionId} en '{destDir}'.");
    }

    public async Task MoveToExactAsync(
        string sourcePath,
        string destinationPath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(destination))
            throw new IOException($"El destino ya existe y no se sobrescribe: '{destination}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (SameVolume(source, destination))
        {
            File.Move(source, destination);
            return;
        }

        // Copy+delete entre volúmenes. Si el proceso cae tras copiar y antes de borrar,
        // la reconciliación (caso C) NO borra el origen: puede ser una nueva llegada.
        File.Copy(source, destination, overwrite: false);
        if (!HashEquals(destination, expectedHash))
        {
            TryDelete(destination);
            throw new IOException($"La copia de '{source}' a '{destination}' no conservó el hash.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        File.Delete(source);
    }

    public bool Exists(string path)
    {
        try
        {
            return File.Exists(Path.GetFullPath(path));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public string? TryComputeHash(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                return null;
            return Sha256FileHasher.ComputeHex(File.ReadAllBytes(full));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string DestinationDirectory(ArchiveRequest request)
    {
        var y = request.FolderDate.Year.ToString("0000");
        var m = request.FolderDate.Month.ToString("00");
        var d = request.FolderDate.Day.ToString("00");
        if (request.Kind == ArchiveKind.Error)
            return Path.Combine(_paths.Errors, y, m, d);

        var tienda = request.Tienda is int value ? value.ToString() : SinTiendaFolder;
        return Path.Combine(_paths.Processed, y, m, d, tienda);
    }

    private static IEnumerable<string> Candidates(string directory, string originalName, long receptionId)
    {
        yield return Path.Combine(directory, originalName);
        var stem = Path.GetFileNameWithoutExtension(originalName);
        yield return Path.Combine(directory, $"{stem}_R{receptionId}.xml");
        for (var i = 2; i <= 50; i++)
            yield return Path.Combine(directory, $"{stem}_R{receptionId}_{i}.xml");
    }

    private static bool SameVolume(string source, string destination)
    {
        var srcRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var dstRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        return srcRoot is not null
               && dstRoot is not null
               && string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashEquals(string path, string expectedHash)
    {
        try
        {
            return string.Equals(
                Sha256FileHasher.ComputeHex(File.ReadAllBytes(path)),
                expectedHash,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
