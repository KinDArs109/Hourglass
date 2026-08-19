using System.Security.Cryptography;
using System.Text;

namespace Hourglass.Utilities;

/// <summary>
/// Wraps DPAPI so tokens on disk are only readable by the Windows user that stored them.
/// </summary>
public static class SecretProtector
{
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return null;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(protectedText);
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
