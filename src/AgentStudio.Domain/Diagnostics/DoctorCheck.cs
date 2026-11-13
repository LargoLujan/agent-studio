using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Diagnostics;

/// <summary>
/// The state of a check of Auto Doctor (docs/20 D-01). The order is the one of the design: a value is never renumbered, because tests and
/// logs name them. <see cref="DoctorStatusRules.Severity"/> is the weight: «Sin comprobar» weighs more than «correcto», so nothing looks
/// healthy for not having been looked at.
/// </summary>
public enum DoctorStatus
{
    Ok,
    Warning,
    Error,
    Unknown,
    NotApplicable,
}

/// <summary>The sections of the panel of Auto Doctor (docs/20 D-01 and D-04).</summary>
public enum DoctorSection
{
    Providers,
    Tools,
    Project,
    DeclaredEnvironment,
    Containers,
}

/// <summary>How the states weigh against each other.</summary>
public static class DoctorStatusRules
{
    /// <summary>The weight of a state: not applicable, then correct, then not checked, then warning, then error.</summary>
    public static int Severity(DoctorStatus status) => status switch
    {
        DoctorStatus.NotApplicable => 0,
        DoctorStatus.Ok => 1,
        DoctorStatus.Unknown => 2,
        DoctorStatus.Warning => 3,
        DoctorStatus.Error => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown state of a check."),
    };

    /// <summary>The worst state of a list (what a section shows when it is folded); not applicable when the list is empty.</summary>
    public static DoctorStatus Worst(IEnumerable<DoctorStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var worst = DoctorStatus.NotApplicable;
        foreach (var status in statuses)
        {
            if (Severity(status) > Severity(worst))
            {
                worst = status;
            }
        }

        return worst;
    }
}

/// <summary>
/// The shape of the stable codes of Auto Doctor and of Auto Graph (<c>doctor.dotnet.sdk</c>, <c>graph.outside-root</c>): lowercase words
/// and digits joined by single <c>.</c> or <c>-</c>. It is the same shape the texts of the interface use for their codes (ADR-0054); the
/// domain cannot call the application, so the rule is written here.
/// </summary>
public static class DoctorCodes
{
    /// <summary>The longest code.</summary>
    public const int MaxLength = 80;

    public static bool IsCode([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxLength || !char.IsAsciiLetterLower(code[0]))
        {
            return false;
        }

        var previousWasSeparator = false;
        foreach (var c in code)
        {
            if (c is '.' or '-')
            {
                if (previousWasSeparator)
                {
                    return false;
                }

                previousWasSeparator = true;
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                previousWasSeparator = false;
            }
            else
            {
                return false;
            }
        }

        return !previousWasSeparator;
    }

    /// <summary>The code itself, or an <see cref="ArgumentException"/> that names the parameter; with <paramref name="prefix"/>, it must start with it (<c>doctor.</c>, <c>graph.</c>).</summary>
    /// <exception cref="ArgumentException">It is not a code.</exception>
    public static string Require(string? code, string parameterName, string? prefix = null)
    {
        if (!IsCode(code) || (prefix is not null && !code.StartsWith(prefix, StringComparison.Ordinal)))
        {
            // The message never quotes the value: a code may come from a file or from the output of a tool, and a message goes to logs.
            throw new ArgumentException($"The value is not a code{(prefix is null ? string.Empty : $" that starts with '{prefix}'")}: lowercase words joined by '.' and '-', at most {MaxLength} characters.", parameterName);
        }

        return code;
    }
}

/// <summary>
/// A fact of a check: a name and a value, such as the version or the path of a tool, or what a project declares. The value may come from a
/// file of the repository, so it is data and not text of the system (docs/20 D-06): one line, with a cap of <see cref="MaxValueLength"/>
/// characters, never an order for a command line. <see cref="Of"/> cuts what is longer; the constructor refuses it.
/// </summary>
public sealed record DoctorFact
{
    /// <summary>The longest value of a fact (docs/20 D-06).</summary>
    public const int MaxValueLength = 200;

    /// <summary>The longest name of a fact.</summary>
    public const int MaxNameLength = 40;

    private const string Ellipsis = "…";

    /// <exception cref="ArgumentException">A name that is not lowercase words joined by <c>.</c> and <c>-</c>, or a value that is empty, spans lines, holds a control character or is longer than <see cref="MaxValueLength"/>.</exception>
    public DoctorFact(string name, string value)
    {
        if (!DoctorCodes.IsCode(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"The value is not the name of a fact: lowercase words joined by '.' and '-', up to {MaxNameLength} characters.", nameof(name));
        }

        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength || UntrustedText.HasHidden(value))
        {
            throw new ArgumentException($"The value of a fact is one line of 1 to {MaxValueLength} characters, with no control characters.", nameof(value));
        }

        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }

    /// <summary>
    /// A fact from a value that was read from somewhere and is not trusted: control characters become spaces and what is longer than the cap
    /// is cut, with «…». An empty value is refused.
    /// </summary>
    public static DoctorFact Of(string name, string untrustedValue)
    {
        ArgumentNullException.ThrowIfNull(untrustedValue);
        var clean = UntrustedText.Clean(untrustedValue).Trim();
        if (clean.Length > MaxValueLength)
        {
            // Never cut a surrogate pair in half.
            var length = MaxValueLength - Ellipsis.Length;
            if (char.IsHighSurrogate(clean[length - 1]))
            {
                length--;
            }

            clean = clean[..length] + Ellipsis;
        }

        return new DoctorFact(name, clean);
    }
}

/// <summary>
/// A check of Auto Doctor (docs/20 D-01): a stable code (<c>doctor.dotnet.sdk</c>), its section, its state, its facts and, if it has one, the
/// code of its fix (D-10). The title and the explanation are not here: the desktop paints them from the code, with the facts as parameters
/// (ADR-0054). Two checks are equal when their parts are, facts in order.
/// </summary>
public sealed record DoctorCheck
{
    /// <summary>The most facts a check holds.</summary>
    public const int MaxFacts = 40;

    /// <exception cref="ArgumentException">A code that is not one of the <c>doctor.</c> codes, a section or a state that does not exist, too many facts or a fix code that is not a code.</exception>
    public DoctorCheck(string code, DoctorSection section, DoctorStatus status, IReadOnlyList<DoctorFact> facts, string? fixCode = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Code = DoctorCodes.Require(code, nameof(code), "doctor.");
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown section of Auto Doctor.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown state of a check.");
        }

        if (facts.Count > MaxFacts || facts.Any(f => f is null))
        {
            throw new ArgumentException($"A check holds up to {MaxFacts} facts and none of them is null.", nameof(facts));
        }

        Section = section;
        Status = status;
        Facts = [.. facts];
        FixCode = fixCode is null ? null : DoctorCodes.Require(fixCode, nameof(fixCode));
    }

    public string Code { get; }

    public DoctorSection Section { get; }

    public DoctorStatus Status { get; }

    public IReadOnlyList<DoctorFact> Facts { get; }

    /// <summary>The code of the fix that goes with the check, or null (docs/20 D-10).</summary>
    public string? FixCode { get; }

    /// <summary>The value of the fact called <paramref name="name"/>, or null when the check has none.</summary>
    public string? Fact(string name) => Facts.FirstOrDefault(f => f.Name == name)?.Value;

    public bool Equals(DoctorCheck? other) =>
        other is not null && Code == other.Code && Section == other.Section && Status == other.Status
        && FixCode == other.FixCode && Facts.SequenceEqual(other.Facts);

    public override int GetHashCode() => HashCode.Combine(Code, Section, Status, Facts.Count);
}

/// <summary>
/// What Auto Doctor found when it ran: the checks in the order the sections show them. Not saved anywhere (docs/20 §5): it is calculated
/// when the panel opens.
/// </summary>
public sealed record DoctorReport
{
    public DoctorReport(IReadOnlyList<DoctorCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Any(c => c is null))
        {
            throw new ArgumentException("A report holds no null check.", nameof(checks));
        }

        var codes = checks.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        if (codes.Count != checks.Count)
        {
            throw new ArgumentException("A report holds a check once: two checks have the same code.", nameof(checks));
        }

        Checks = [.. checks];
    }

    public IReadOnlyList<DoctorCheck> Checks { get; }

    /// <summary>The worst state of a section; not applicable when it has no check.</summary>
    public DoctorStatus WorstOf(DoctorSection section) =>
        DoctorStatusRules.Worst(Checks.Where(c => c.Section == section).Select(c => c.Status));

    /// <summary>The worst state of the whole report.</summary>
    public DoctorStatus Worst => DoctorStatusRules.Worst(Checks.Select(c => c.Status));

    public bool Equals(DoctorReport? other) => other is not null && Checks.SequenceEqual(other.Checks);

    public override int GetHashCode() => HashCode.Combine(Checks.Count);
}
