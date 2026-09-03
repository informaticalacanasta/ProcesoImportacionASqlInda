using System.Security.Cryptography;

namespace DbInda.Worker.Files;

public static class Sha256FileHasher
{
    public static string ComputeHex(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
