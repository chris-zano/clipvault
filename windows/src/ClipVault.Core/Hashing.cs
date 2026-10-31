using System.Security.Cryptography;
using System.Text;

namespace ClipVault.Core;

internal static class Hashing
{
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    public static string HumanSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
