using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The end of the result line of the journal that names the <c>AgentRunStarted</c> of the launch (docs/17 D-02): the
/// contract between the writer of the journal and the derivation that anchors it as memory. It goes last, any value
/// round-trips, and a note can never fake it.
/// </summary>
public sealed class JournalResultAnchorTests
{
    private const string Head = "  - Resultado P-0007 · 11:44:31 · Succeeded · 144,2 s · tokens 18.300 → 2.150 (exactos)";

    [Fact]
    public void The_end_names_the_run_the_task_and_the_attempt()
    {
        Assert.Equal("ejecución `20261015-a1b2c3` · tarea `T-12` · intento 1", JournalResultAnchor.Format(new AgentRunAnchor("20261015-a1b2c3", "T-12", 1)));
        Assert.Equal("ejecución `aux-20261030-0a1b2c` · intento 2", JournalResultAnchor.Format(new AgentRunAnchor("aux-20261030-0a1b2c", null, 2)));
    }

    [Theory]
    [InlineData("20261015-a1b2c3", "T-12", 1)]
    [InlineData("deb-20261019-a1b2c3", "r1-arquitecto", 2)]
    [InlineData("aud-20261019-0a1b2c", "juez-juez-revision", 1)]
    [InlineData("20261015-a1b2c3", null, 3)]
    [InlineData("20261015-a1b2c3", "tarea_con_guion_bajo", 12)]
    [InlineData("deb-x", "r1-mi agente · con «comillas» y `tildes`", 999_999_999)]
    public void Any_anchor_round_trips_through_a_result_line(string run, string? task, int attempt)
    {
        var anchor = new AgentRunAnchor(run, task, attempt);
        var line = Head + " · " + JournalResultAnchor.Format(anchor);

        Assert.True(JournalResultAnchor.TryParse(line, out var read));
        Assert.Equal(anchor, read);
        Assert.True(JournalResultAnchor.TryParse(line + "\r", out var withCarriageReturn));
        Assert.Equal(anchor, withCarriageReturn);
        Assert.Matches("^ejecución `[A-Za-z0-9._~%-]+`( · tarea `[A-Za-z0-9._~%-]+`)? · intento [1-9][0-9]*$", JournalResultAnchor.Format(anchor));
    }

    [Theory]
    [InlineData(Head)] // a line of before Phase 12
    [InlineData(Head + " · ejecución 20261015-a1b2c3 · tarea T-12 · intento 1")] // what a note could write: no backticks
    [InlineData(Head + " · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 1 · nota")] // not at the end
    [InlineData(Head + " · ejecución `20261015-a1b2c3` · intento 0")]
    [InlineData(Head + " · ejecución `20261015-a1b2c3` · intento 01")]
    [InlineData(Head + " · ejecución `` · intento 1")]
    [InlineData(Head + " · ejecución `20261015 a1b2c3` · intento 1")]
    [InlineData(Head + " · ejecución `%20` · intento 1")] // decodes to a blank run
    [InlineData("")]
    [InlineData(null)]
    public void A_line_that_does_not_end_with_an_anchor_has_none(string? line)
    {
        Assert.False(JournalResultAnchor.TryParse(line, out var anchor));
        Assert.Null(anchor);
    }

    [Fact]
    public void An_anchor_names_a_run_and_a_positive_attempt()
    {
        Assert.Throws<ArgumentException>(() => new AgentRunAnchor(" ", "T1", 1));
        Assert.Throws<ArgumentException>(() => new AgentRunAnchor("r", " ", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunAnchor("r", "T1", 0));
        Assert.Equal(new AgentRunAnchor("r", "T1", 2), new AgentRunAnchor("r", "T1", 2));
        Assert.NotEqual(new AgentRunAnchor("r", "T1", 2), new AgentRunAnchor("r", null, 2));
    }
}
