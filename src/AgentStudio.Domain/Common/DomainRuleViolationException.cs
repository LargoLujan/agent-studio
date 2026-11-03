namespace AgentStudio.Domain.Common;

/// <summary>
/// Raised when an operation would break a domain invariant (an illegal state transition, a dependency cycle,
/// completing a task without evidence...). It signals a programming or orchestration error, never a user typo.
/// </summary>
public class DomainRuleViolationException : InvalidOperationException
{
    public DomainRuleViolationException(string rule, string message)
        : base(message)
    {
        Rule = rule;
    }

    public DomainRuleViolationException()
    {
        Rule = string.Empty;
    }

    public DomainRuleViolationException(string message)
        : base(message)
    {
        Rule = string.Empty;
    }

    public DomainRuleViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Rule = string.Empty;
    }

    /// <summary>Stable code of the violated rule, useful for logs and tests.</summary>
    public string Rule { get; }
}
