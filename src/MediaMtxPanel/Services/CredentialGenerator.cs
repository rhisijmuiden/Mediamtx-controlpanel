using System.Security.Cryptography;

namespace MediaMtxPanel;

public static class CredentialGenerator
{
    // No ambiguous characters and nothing that needs escaping in URLs, RTSP userinfo or SRT stream ids.
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Password(int length = 20) => RandomNumberGenerator.GetString(Alphabet, length);

    public static string UserName(string prefix) =>
        $"{prefix}_{RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 6)}";

    /// <summary>Lowercase random id for public URLs.</summary>
    public static string Slug(int length = 16) => RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", length);

    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    }
}
