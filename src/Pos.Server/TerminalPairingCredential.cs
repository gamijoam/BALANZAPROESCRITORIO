using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace Pos.Server;

public static class TerminalPairingPolicy
{
    public static bool RequiresCredential(bool requireForAll, IPAddress? remoteAddress)
        => requireForAll;

    public static bool RequiresHttps(IPAddress? remoteAddress)
        => remoteAddress is null || !IPAddress.IsLoopback(remoteAddress);
}

public static class TerminalPairingCredential
{
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))).ToLowerInvariant();
    }

    public static bool Matches(string? candidate, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length != 64 ||
            candidate.Any(character => !Uri.IsHexDigit(character)) ||
            string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 ||
            expectedHash.Any(character => !Uri.IsHexDigit(character))) return false;

        var candidateHash = Convert.FromHexString(Hash(candidate));
        var storedHash = Convert.FromHexString(expectedHash);
        try { return CryptographicOperations.FixedTimeEquals(candidateHash, storedHash); }
        finally
        {
            CryptographicOperations.ZeroMemory(candidateHash);
            CryptographicOperations.ZeroMemory(storedHash);
        }
    }
}
