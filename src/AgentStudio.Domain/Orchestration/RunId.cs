namespace AgentStudio.Domain.Orchestration;

/// <summary>
/// Identifier of one orchestration run, such as <c>20261015-a1b2c3</c>. It ends up in git refs, folder names and the
/// skills session folder, so only <c>[A-Za-z0-9_-]</c> is accepted, 1 to 64 characters.
/// </summary>
public readonly record struct RunId
{
    public const int MaxLength = 64;

    public RunId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxLength || !value.All(IsAllowed))
        {
            throw new ArgumentException($"A run id has 1 to {MaxLength} characters from [A-Za-z0-9_-]: '{value}'.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsAllowed(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
