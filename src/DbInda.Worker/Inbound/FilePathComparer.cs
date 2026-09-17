namespace DbInda.Worker.Inbound;

/// <summary>
/// Comparador de identidad física de rutas según el filesystem del SO.
/// Windows: case-insensitive. Linux: case-sensitive.
/// No usar para extensiones XML ni para valores de negocio.
/// </summary>
public static class FilePathComparer
{
    public static StringComparer ForIdentity { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
