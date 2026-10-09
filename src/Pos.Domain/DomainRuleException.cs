namespace Pos.Domain;

/// <summary>Indicates that a requested operation violates a domain invariant.</summary>
public sealed class DomainRuleException : InvalidOperationException
{
    public DomainRuleException(string message) : base(message) { }
}
