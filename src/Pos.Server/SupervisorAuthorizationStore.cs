using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pos.Domain.Identity;

public sealed record AdminPinChallenge(string Error, string Permission, Guid OperationId, string Operation, string RequestFingerprint);

public sealed class SupervisorAuthorizationStore
{
    private readonly ConcurrentDictionary<string, Approval> _approvals = new(StringComparer.Ordinal);

    public string Issue(Guid operatorId, Guid approverId, Guid terminalId, Permission permission, string operation, string requestFingerprint, Guid operationId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _approvals)
            if (entry.Value.DomainApproval.ExpiresAtUtc <= now) _approvals.TryRemove(entry.Key, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var expires = now.AddSeconds(60);
        var domainApproval = SupervisorApproval.Issue(permission, approverId, operationId, expires);
        _approvals[tokenHash] = new Approval(operatorId, terminalId, permission, operation, requestFingerprint, domainApproval);
        return token + "." + operationId.ToString("N");
    }

    public bool TryConsume(string? token, Guid operatorId, Guid terminalId, Permission permission, string operation, string requestFingerprint)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out var operationId)) return false;
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts[0]))).ToLowerInvariant();
        if (!_approvals.TryGetValue(tokenHash, out var approval) || approval.OperatorId != operatorId || approval.TerminalId != terminalId || approval.Permission != permission ||
            !string.Equals(approval.Operation, operation, StringComparison.Ordinal) ||
            !string.Equals(approval.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)) return false;
        if (!_approvals.TryRemove(tokenHash, out var consumed) || !ReferenceEquals(consumed, approval)) return false;
        return approval.DomainApproval.TryConsume(permission, operationId, DateTimeOffset.UtcNow);
    }

    private sealed record Approval(Guid OperatorId, Guid TerminalId, Permission Permission, string Operation, string RequestFingerprint, SupervisorApproval DomainApproval);
}
