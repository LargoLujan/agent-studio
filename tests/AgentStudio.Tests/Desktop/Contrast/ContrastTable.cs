using System.Globalization;
using System.IO;
using System.Text;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>One pair of colours that was measured (or deliberately exempted) with the reason it is listed.</summary>
internal sealed record ContrastRow(
    string Area,
    string Id,
    string Usage,
    ContrastKind Kind,
    string ForegroundName,
    Rgba Foreground,
    string BackgroundName,
    Rgba Background,
    double Ratio,
    string? ExemptReason)
{
    public bool IsExempt => ExemptReason is not null;

    public double Minimum => Kind.Minimum();

    public bool Passes => IsExempt || Ratio >= Minimum;

    public string Status => IsExempt ? "exento" : Passes ? "ok" : "FALLA";

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Area}] {Id}: {ForegroundName} {Foreground} sobre {BackgroundName} {Background} = {Ratio:0.000}:1 (mínimo {Minimum:0.0}:1, {Kind.Label()}) — {Usage}");
}

/// <summary>
/// The pairs of one area of the theme. Every test builds its table, asserts that nothing fails and, when the
/// environment variable <c>AGENTSTUDIO_CONTRAST_REPORT</c> names a folder, writes the table there as TSV so that
/// docs/12-accesibilidad-tema-oscuro.md can quote the real ratios.
/// </summary>
internal sealed class ContrastTable(string area)
{
    public const string ReportVariable = "AGENTSTUDIO_CONTRAST_REPORT";

    private readonly List<ContrastRow> _rows = [];

    public IReadOnlyList<ContrastRow> Rows => _rows;

    /// <summary>
    /// Measures <paramref name="foreground"/> on <paramref name="background"/>. A translucent foreground (or a
    /// translucent background over <paramref name="behind"/>) is composited first, as the screen would show it.
    /// </summary>
    public ContrastTable Add(string id, string usage, ContrastKind kind, string foregroundName, Rgba foreground, string backgroundName, Rgba background, Rgba? behind = null)
    {
        _rows.Add(Measure(id, usage, kind, foregroundName, foreground, backgroundName, background, behind, exemptReason: null));
        return this;
    }

    /// <summary>Lists a pair that WCAG does not require to pass; the ratio is still measured and reported.</summary>
    public ContrastTable Exempt(string id, string usage, string reason, string foregroundName, Rgba foreground, string backgroundName, Rgba background, ContrastKind kind = ContrastKind.UiComponent)
    {
        _rows.Add(Measure(id, usage, kind, foregroundName, foreground, backgroundName, background, behind: null, reason));
        return this;
    }

    public IReadOnlyList<ContrastRow> Failures => [.. _rows.Where(r => !r.Passes)];

    /// <summary>Fails with every failing pair listed, so one red run shows the whole picture.</summary>
    public void AssertAllPass()
    {
        Write();
        Assert.NotEmpty(_rows);
        var failures = Failures;
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {_rows.Count(r => !r.IsExempt)} pairs in '{area}' fail WCAG AA:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Select(f => "  " + f)));
    }

    /// <summary>Writes <c>&lt;area&gt;.tsv</c> to the folder named by <see cref="ReportVariable"/>, when set.</summary>
    public void Write()
    {
        var folder = Environment.GetEnvironmentVariable(ReportVariable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var text = new StringBuilder();
        text.AppendLine("area\tid\tusage\tkind\tforeground\tforegroundColour\tbackground\tbackgroundColour\tratio\tminimum\tstatus\treason");
        foreach (var r in _rows)
        {
            text.Append(string.Join('\t', r.Area, r.Id, r.Usage, r.Kind.Label(), r.ForegroundName, r.Foreground, r.BackgroundName, r.Background))
                .Append('\t').Append(r.Ratio.ToString("0.000", CultureInfo.InvariantCulture))
                .Append('\t').Append(r.Minimum.ToString("0.0", CultureInfo.InvariantCulture))
                .Append('\t').Append(r.Status)
                .Append('\t').AppendLine(r.ExemptReason ?? string.Empty);
        }

        File.WriteAllText(Path.Combine(folder, area + ".tsv"), text.ToString(), new UTF8Encoding(false));
    }

    private ContrastRow Measure(string id, string usage, ContrastKind kind, string foregroundName, Rgba foreground, string backgroundName, Rgba background, Rgba? behind, string? exemptReason)
    {
        if (_rows.Any(r => r.Id == id))
        {
            throw new InvalidOperationException($"Duplicate pair id '{id}' in '{area}'.");
        }

        var solidBackground = background.IsOpaque ? background : Wcag.Over(background, behind ?? throw new ArgumentException($"'{backgroundName}' is translucent; pass the colour behind it.", nameof(behind)));
        var solidForeground = foreground.IsOpaque ? foreground : Wcag.Over(foreground, solidBackground);
        return new ContrastRow(area, id, usage, kind, foregroundName, foreground, backgroundName, background, Wcag.Ratio(solidForeground, solidBackground), exemptReason);
    }
}
