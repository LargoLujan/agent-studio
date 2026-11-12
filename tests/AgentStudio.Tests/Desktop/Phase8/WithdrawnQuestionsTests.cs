using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Desktop.Lock;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The questions the lock of the interface withdraws (docs/16 D-13, ADR-0037): the token of <see cref="LockWithdrawal"/> is
/// cancelled when the interface locks, already is when it is locked, and stays so once unlocked; and the question about unsaved
/// edits, which the lock withdraws, offers «Guardar» only when every document can be saved from here.
/// </summary>
public sealed class WithdrawnQuestionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_question_is_withdrawn_when_the_interface_locks_and_stays_withdrawn_once_unlocked()
    {
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        using var withdrawal = new LockWithdrawal(interfaceLock);
        var before = withdrawal.Token.IsCancellationRequested;

        await interfaceLock.LockAsync(LockReason.Idle, Ct);
        var locked = withdrawal.Token.IsCancellationRequested;
        await interfaceLock.UnlockWithHelloAsync(Ct);

        Assert.False(before);
        Assert.True(locked);
        Assert.True(withdrawal.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_question_asked_while_locked_is_withdrawn_before_it_is_shown_and_the_callers_token_withdraws_it_too()
    {
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), new FakeTimeProvider(T0));
        await interfaceLock.LockAsync(LockReason.Manual, Ct);
        using var whileLocked = new LockWithdrawal(interfaceLock);
        using var caller = new CancellationTokenSource();
        using var withoutLock = new LockWithdrawal(null, caller.Token);
        var beforeCaller = withoutLock.Token.IsCancellationRequested;
        await caller.CancelAsync();

        Assert.True(whileLocked.Token.IsCancellationRequested);
        Assert.False(beforeCaller);
        Assert.True(withoutLock.Token.IsCancellationRequested);
    }

    [Fact]
    public void The_question_about_unsaved_edits_lists_ten_titles_and_offers_guardar_only_when_everything_can_be_saved()
    {
        var titles = Enumerable.Range(1, 12).Select(i => $"fichero{i}.cs").ToList();
        var savable = new UnsavedChangesPromptViewModel(titles, canSave: true);
        var notSavable = new UnsavedChangesPromptViewModel(["notas.txt"], canSave: false);

        notSavable.ChooseCommand.Execute(UnsavedChangesChoice.Save);
        var ignored = notSavable.Answer;
        var closes = 0;
        savable.CloseRequested += (_, _) => closes++;
        savable.ChooseCommand.Execute(UnsavedChangesChoice.Discard);

        Assert.Equal(11, savable.Listed.Count);
        Assert.Equal("• fichero1.cs", savable.Listed[0]);
        Assert.Equal("… y 2 más", savable.Listed[^1]);
        Assert.Equal(("Tienes cambios sin guardar en:", "¿Quieres guardarlos?", "Descartar cambios", "Cancelar"), (savable.Intro, savable.Question, savable.DiscardText, savable.CancelText));
        Assert.Equal(("Descartar cambios", "Volver"), (notSavable.DiscardText, notSavable.CancelText));
        Assert.Null(ignored);
        Assert.Equal(UnsavedChangesChoice.Discard, savable.Answer);
        Assert.Equal(1, closes);
    }
}
