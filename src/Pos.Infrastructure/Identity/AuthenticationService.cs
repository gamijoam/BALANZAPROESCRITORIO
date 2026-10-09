using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Persistence;
using Pos.Domain.Identity;

namespace Pos.Infrastructure.Identity;

public sealed class AuthenticationService(PosDbContext db)
{
    public const int Iterations = 600_000;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private const int MaximumFailures = 5;

    public async Task<Guid> CreateOperatorAsync(string? username, string? pin, CancellationToken cancellationToken = default, OperatorRole role = OperatorRole.Administrator)
    {
        var normalized = NormalizeUsername(username);
        ValidatePin(pin);
        var validatedPin = pin!;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(validatedPin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        var user = new OperatorUserRecord { Id = Guid.NewGuid(), Username = normalized, Role = role.ToString(), PinSalt = salt, PinHash = hash, CreatedAtUtc = DateTime.UtcNow };
        db.OperatorUsers.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    public async Task<AuthenticationResult?> AuthenticateAsync(string? username, string? pin, Guid terminalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 12 || pin.Any(c => c is < '0' or > '9'))
        {
            _ = Rfc2898DeriveBytes.Pbkdf2(pin is { Length: <= 12 } ? Encoding.UTF8.GetBytes(pin) : [], new byte[16], Iterations, HashAlgorithmName.SHA256, 32);
            return null;
        }
        var normalized = NormalizeUsername(username);
        var user = await db.OperatorUsers.SingleOrDefaultAsync(x => x.Username == normalized, cancellationToken);
        if (user is null)
        {
            _ = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), new byte[16], Iterations, HashAlgorithmName.SHA256, 32);
            return null;
        }

        var now = DateTime.UtcNow;
        if (!user.Active || (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc > now)) return null;
        var candidate = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), user.PinSalt, Iterations, HashAlgorithmName.SHA256, user.PinHash.Length);
        if (!CryptographicOperations.FixedTimeEquals(candidate, user.PinHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaximumFailures)
            {
                user.FailedLoginCount = 0;
                user.LockoutUntilUtc = now.Add(LockoutDuration);
            }
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        user.FailedLoginCount = 0;
        user.LockoutUntilUtc = null;
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var session = new AccessSessionRecord
        {
            Id = Guid.NewGuid(), OperatorId = user.Id, TerminalId = terminalId,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant(),
            CreatedAtUtc = now, ExpiresAtUtc = now.Add(SessionLifetime)
        };
        db.AccessSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new AuthenticationResult(rawToken, session.ExpiresAtUtc, user.Id, terminalId);
    }

    public async Task<AccessSessionRecord?> FindValidSessionAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
        var now = DateTime.UtcNow;
        return await db.AccessSessions.SingleOrDefaultAsync(x => x.TokenHash == tokenHash && x.RevokedAtUtc == null && x.ExpiresAtUtc > now, cancellationToken);
    }

    public async Task RevokeSessionAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await FindValidSessionAsync(rawToken, cancellationToken);
        if (session is null) return;
        session.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PermissionSet> GetPermissionsAsync(Guid operatorId, CancellationToken cancellationToken = default)
    {
        var roleName = await db.OperatorUsers.AsNoTracking().Where(x => x.Id == operatorId).Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        return Enum.TryParse<OperatorRole>(roleName, ignoreCase: false, out var role) && Enum.IsDefined(role)
            ? RolePermissionPolicy.For(role)
            : new PermissionSet();
    }

    public async Task<bool> HasPermissionAsync(Guid operatorId, Permission permission, CancellationToken cancellationToken = default)
        => (await GetPermissionsAsync(operatorId, cancellationToken)).Can(permission);

    public async Task<OperatorRole?> GetRoleAsync(Guid operatorId, CancellationToken cancellationToken = default)
    {
        var roleName = await db.OperatorUsers.AsNoTracking().Where(x => x.Id == operatorId).Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        return Enum.TryParse<OperatorRole>(roleName, ignoreCase: false, out var role) && Enum.IsDefined(role) ? role : null;
    }

    public static string NormalizeUsername(string? username)
    {
        if (username is null) throw new ArgumentException("Username is required.", nameof(username));
        var normalized = username.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 64 || normalized.Any(char.IsControl)) throw new ArgumentException("Username must contain between 3 and 64 printable characters.", nameof(username));
        return normalized;
    }

    private static void ValidatePin(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 12 || pin.Any(c => c is < '0' or > '9')) throw new ArgumentException("PIN must contain 4 to 12 digits.", nameof(pin));
    }
}

public sealed record AuthenticationResult(string Token, DateTime ExpiresAtUtc, Guid OperatorId, Guid TerminalId);
