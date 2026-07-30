using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.DistributedTranscode.Security;

public static class WorkerRequestAuthenticator
{
    public const string SignatureHeaderName = "X-DistributedTranscode-Signature";
    public const string TimestampHeaderName = "X-DistributedTranscode-Timestamp";

    public static string CreateSignature(string body, string timestamp, string sharedSecret)
    {
        var payload = $"{timestamp}\n{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret ?? string.Empty));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    public static bool IsAuthorized(
        string body,
        string? timestamp,
        string? providedSignature,
        string sharedSecret,
        TimeSpan allowedClockSkew)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(providedSignature))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(timestamp, out var parsedTimestamp))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - parsedTimestamp > allowedClockSkew || parsedTimestamp - now > allowedClockSkew)
        {
            return false;
        }

        var expected = CreateSignature(body, timestamp, sharedSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(providedSignature));
    }
}
