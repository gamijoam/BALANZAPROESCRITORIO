using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

public sealed class AdminWebAuthentication
{
    public const string SessionCookieName = "__Host-pos-license-admin";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._-]{3,64}$", RegexOptions.CultureInvariant);
    private readonly byte[] _passwordSalt;
    private readonly byte[] _passwordHash;
    private readonly byte[] _totpSecret;
    private readonly byte[] _sessionKey;
    private readonly ConcurrentDictionary<string, LoginAttempt> _attempts = new(StringComparer.Ordinal);

    private AdminWebAuthentication()
    {
        Enabled = false;
        Username = string.Empty;
        _passwordSalt = [];
        _passwordHash = [];
        _totpSecret = [];
        _sessionKey = [];
    }

    private AdminWebAuthentication(AdminWebSecret secret)
    {
        if (string.IsNullOrWhiteSpace(secret.Username) || !UsernamePattern.IsMatch(secret.Username) ||
            !TryDecode(secret.PasswordSalt, out var passwordSalt) || passwordSalt.Length < 16 ||
            !TryDecode(secret.PasswordHash, out var passwordHash) || passwordHash.Length != 32 ||
            !TryDecodeBase32(secret.TotpSecret, out var totpSecret) || totpSecret.Length < 20 ||
            !TryDecode(secret.SessionHmacKey, out var sessionKey) || sessionKey.Length < 32)
            throw new InvalidOperationException("La configuración del administrador web de licencias no es válida.");
        _passwordSalt = passwordSalt;
        _passwordHash = passwordHash;
        _totpSecret = totpSecret;
        _sessionKey = sessionKey;
        Username = secret.Username;
        Enabled = true;
    }

    public bool Enabled { get; }
    public string Username { get; }

    public static AdminWebAuthentication Load(IConfiguration configuration)
    {
        var path = configuration["LicensingVps:AdminWebConfigPath"];
        if (string.IsNullOrWhiteSpace(path)) return new AdminWebAuthentication();
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("LicensingVps:AdminWebConfigPath debe ser una ruta absoluta.");
        if (!File.Exists(path)) return new AdminWebAuthentication();
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode unsafeBits = UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & unsafeBits) != 0)
                throw new InvalidOperationException("El archivo del administrador web debe ser legible por root y el servicio, sin acceso de escritura del grupo ni acceso de otros usuarios.");
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > 16 * 1024)
                throw new InvalidOperationException("El archivo de administrador web está vacío o supera 16 KiB.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("enabled", out var enabledElement) ||
                enabledElement.ValueKind != JsonValueKind.True)
                return new AdminWebAuthentication();
            var secret = JsonSerializer.Deserialize<AdminWebSecret>(bytes, JsonOptions)
                ?? throw new InvalidOperationException("El archivo de administrador web está vacío.");
            return new AdminWebAuthentication(secret);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("El archivo de administrador web no es JSON válido.", exception);
        }
    }

    public bool TryLogin(string username, string password, string totpCode, string remoteAddress,
        DateTimeOffset nowUtc, out AdminWebSession? session, out bool rateLimited)
    {
        session = null;
        rateLimited = false;
        if (!Enabled) return false;
        var address = string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress;
        if (_attempts.Count > 10_000) _attempts.Clear();
        var attempt = _attempts.GetOrAdd(address, _ => new LoginAttempt());
        lock (attempt)
        {
            if (attempt.BlockedUntilUtc > nowUtc)
            {
                rateLimited = true;
                return false;
            }
            if (attempt.BlockedUntilUtc != default && attempt.BlockedUntilUtc <= nowUtc)
            {
                attempt.Failures = 0;
                attempt.BlockedUntilUtc = default;
            }
        }

        var submittedUsername = username ?? string.Empty;
        var submittedPassword = password ?? string.Empty;
        var passwordIsValid = VerifyPassword(submittedPassword);
        var codeIsValid = VerifyTotp(totpCode ?? string.Empty, nowUtc);
        var usernameIsValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(submittedUsername), Encoding.UTF8.GetBytes(Username));
        if (!(passwordIsValid & codeIsValid & usernameIsValid))
        {
            lock (attempt)
            {
                attempt.Failures++;
                if (attempt.Failures >= 5)
                {
                    attempt.Failures = 0;
                    attempt.BlockedUntilUtc = nowUtc.AddMinutes(15);
                }
            }
            return false;
        }

        lock (attempt)
        {
            attempt.Failures = 0;
            attempt.BlockedUntilUtc = default;
        }
        session = new AdminWebSession(Username, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            nowUtc, nowUtc.Add(SessionLifetime), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return true;
    }

    public void SetSessionCookie(HttpContext context, AdminWebSession session)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var signature = HMACSHA256.HashData(_sessionKey, payload);
        var value = Base64UrlEncode(payload) + "." + Base64UrlEncode(signature);
        context.Response.Cookies.Append(SessionCookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = SessionLifetime
        });
    }

    public void ClearSessionCookie(HttpContext context) => context.Response.Cookies.Delete(SessionCookieName,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });

    public bool TryGetSession(HttpContext context, out AdminWebSession? session)
    {
        session = null;
        if (!Enabled || !context.Request.Cookies.TryGetValue(SessionCookieName, out var value) || string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Length > 8192) return false;
        var separator = value.IndexOf('.');
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf('.', separator + 1) >= 0) return false;
        if (!TryDecodeBase64Url(value[..separator], out var payload) || !TryDecodeBase64Url(value[(separator + 1)..], out var signature))
            return false;
        var expectedSignature = HMACSHA256.HashData(_sessionKey, payload);
        if (signature.Length != expectedSignature.Length || !CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<AdminWebSession>(payload, JsonOptions);
            var nowUtc = DateTimeOffset.UtcNow;
            if (parsed is null || parsed.Username != Username || string.IsNullOrWhiteSpace(parsed.SessionId) ||
                string.IsNullOrWhiteSpace(parsed.CsrfToken) || parsed.IssuedAtUtc.Offset != TimeSpan.Zero ||
                parsed.ExpiresAtUtc.Offset != TimeSpan.Zero || parsed.IssuedAtUtc > nowUtc.AddMinutes(1) ||
                parsed.ExpiresAtUtc <= nowUtc || parsed.ExpiresAtUtc > parsed.IssuedAtUtc.Add(SessionLifetime))
                return false;
            session = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public bool IsAuthorized(HttpContext context, bool requireCsrf, out AdminWebSession? session)
    {
        if (!TryGetSession(context, out session)) return false;
        if (!requireCsrf) return true;
        var provided = context.Request.Headers["X-CSRF-Token"].ToString();
        return FixedTimeTextEquals(provided, session!.CsrfToken);
    }

    private bool VerifyPassword(string password)
    {
        if (password.Length > 1024) return false;
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, _passwordSalt, 310_000, HashAlgorithmName.SHA256, _passwordHash.Length);
        return CryptographicOperations.FixedTimeEquals(derived, _passwordHash);
    }

    private bool VerifyTotp(string code, DateTimeOffset nowUtc)
    {
        if (!Regex.IsMatch(code, "^[0-9]{6}$", RegexOptions.CultureInvariant)) return false;
        var step = nowUtc.ToUnixTimeSeconds() / 30;
        var submitted = Encoding.ASCII.GetBytes(code);
        for (var delta = -1; delta <= 1; delta++)
        {
            var counter = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(step + delta));
            var hash = HMACSHA1.HashData(_totpSecret, counter);
            var offset = hash[^1] & 0x0f;
            var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            var expected = Encoding.ASCII.GetBytes((binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            if (CryptographicOperations.FixedTimeEquals(submitted, expected)) return true;
        }
        return false;
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(value); return true; }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException) { bytes = []; return false; }
    }

    private static bool TryDecodeBase32(string value, out byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = (value ?? string.Empty).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(normalized.Length * 5 / 8);
        var buffer = 0;
        var bitCount = 0;
        foreach (var character in normalized)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0) { bytes = []; return false; }
            buffer = (buffer << 5) | digit;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                output.Add((byte)(buffer >> bitCount));
                buffer &= (1 << bitCount) - 1;
            }
        }
        bytes = output.ToArray();
        return bitCount < 5;
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException) { bytes = []; return false; }
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class LoginAttempt
    {
        public int Failures { get; set; }
        public DateTimeOffset BlockedUntilUtc { get; set; }
    }

    private sealed record AdminWebSecret(bool Enabled, string Username, string PasswordSalt, string PasswordHash,
        string TotpSecret, string SessionHmacKey);
}

public sealed record AdminWebLoginRequest(string Username, string Password, string TotpCode);
public sealed record AdminWebSession(string Username, string SessionId, DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc, string CsrfToken);
