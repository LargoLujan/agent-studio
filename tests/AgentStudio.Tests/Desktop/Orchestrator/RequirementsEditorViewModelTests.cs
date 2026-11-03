using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The «Requisitos» panel: the user writes the requirements and freezes them through the requirement store. A frozen set
/// never changes; editing it is a new version with its own hash.
/// </summary>
public sealed class RequirementsEditorViewModelTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly ProjectContext Project = new("p-1", Root);

    private static FrozenRequirementSet Version1() =>
        FrozenRequirementSet.Freeze(
            [new Requirement(new RequirementId("RQ-01"), "Añadir Calc.Multiply(a, b)", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Multiply(2, 3) devuelve 6", VerificationMethod.AutomatedTest)])],
            T0);

    private static (RequirementsEditorViewModel Editor, FakeRequirementStore Store, List<FrozenRequirementSet> Frozen) Create(params FrozenRequirementSet[] history)
    {
        var store = new FakeRequirementStore(history);
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { Requirements = store });
        var frozen = new List<FrozenRequirementSet>();
        var editor = new RequirementsEditorViewModel(session, new FakeTimeProvider(T0), set =>
        {
            frozen.Add(set);
            return Task.CompletedTask;
        });
        editor.Load(Project, history.Length == 0 ? null : history[^1]);
        return (editor, store, frozen);
    }

    /// <summary>One mandatory requirement with the text and criterion of <see cref="Version1"/>, typed in a new draft.</summary>
    private static void TypeVersion1(RequirementsEditorViewModel editor)
    {
        editor.AddRequirementCommand.Execute(null);
        editor.Drafts[^1].Statement = "Añadir Calc.Multiply(a, b)";
        editor.Drafts[^1].Criteria[0].Description = "Multiply(2, 3) devuelve 6";
    }

    [Fact]
    public void Without_frozen_requirements_an_empty_draft_is_open_and_cannot_be_frozen()
    {
        var (editor, store, _) = Create();

        Assert.True(editor.IsEditing);
        Assert.Null(editor.Frozen);
        Assert.Empty(editor.Drafts);
        Assert.Equal(["Añade al menos un requisito."], editor.Problems);
        Assert.False(editor.FreezeCommand.CanExecute(null));
        Assert.Equal("Sin requisitos congelados: escríbelos y congélalos para poder planificar.", editor.StatusText);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void A_new_requirement_is_mandatory_with_one_automated_criterion_and_says_what_it_lacks()
    {
        var (editor, _, _) = Create();

        editor.AddRequirementCommand.Execute(null);
        editor.AddRequirementCommand.Execute(null);

        Assert.Equal(["RQ-01", "RQ-02"], editor.Drafts.Select(d => d.Id));
        var first = editor.Drafts[0];
        Assert.True(first.IsMandatory);
        var criterion = Assert.Single(first.Criteria);
        Assert.Equal("c1", criterion.Id);
        Assert.Equal(VerificationMethod.AutomatedTest, criterion.SelectedMethod.Value);
        Assert.Contains("El requisito RQ-01 no tiene texto.", editor.Problems);
        Assert.Contains("El criterio c1 de RQ-01 no tiene descripción.", editor.Problems);
        Assert.False(editor.FreezeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Freezing_stores_the_literal_requirements_and_shows_their_hash()
    {
        var (editor, store, frozen) = Create();
        TypeVersion1(editor);
        editor.AddRequirementCommand.Execute(null);
        var second = editor.Drafts[1];
        second.Statement = "  Documentar la operación  ";
        second.IsMandatory = false;
        second.Criteria[0].Description = "README actualizado";
        second.Criteria[0].SelectedMethod = second.Criteria[0].MethodOptions.Single(o => o.Value == VerificationMethod.HumanReview);

        Assert.Empty(editor.Problems);
        await editor.FreezeCommand.ExecuteAsync(null);

        var expected = FrozenRequirementSet.Freeze(
            [
                new Requirement(new RequirementId("RQ-01"), "Añadir Calc.Multiply(a, b)", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Multiply(2, 3) devuelve 6", VerificationMethod.AutomatedTest)]),
                new Requirement(new RequirementId("RQ-02"), "Documentar la operación", RequirementPriority.Desirable, [new AcceptanceCriterion("c1", "README actualizado", VerificationMethod.HumanReview)]),
            ],
            T0);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(expected.ContentHash, saved.ContentHash);
        Assert.Equal([saved], frozen);
        Assert.Same(saved, editor.Frozen);
        Assert.False(editor.IsEditing);
        Assert.Equal(saved.ContentHash, editor.FrozenHash);
        Assert.Contains(saved.ContentHash[..12], editor.StatusText, StringComparison.Ordinal);
        Assert.Null(editor.ErrorMessage);
    }

    [Fact]
    public void Repeated_ids_and_a_mandatory_requirement_without_criteria_cannot_be_frozen()
    {
        var (editor, store, _) = Create();
        TypeVersion1(editor);
        TypeVersion1(editor);
        editor.Drafts[1].Id = "RQ-01";
        editor.Drafts[0].RemoveCriterionCommand.Execute(editor.Drafts[0].Criteria[0]);

        Assert.Contains("El identificador RQ-01 está repetido.", editor.Problems);
        Assert.Contains("El requisito obligatorio RQ-01 necesita al menos un criterio de aceptación.", editor.Problems);
        Assert.False(editor.FreezeCommand.CanExecute(null));
        Assert.Empty(store.Saved);

        editor.Drafts[0].IsMandatory = false;
        editor.Drafts[1].Id = "RQ-02";

        Assert.Empty(editor.Problems);
        Assert.True(editor.FreezeCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_frozen_set_is_read_only_and_editing_it_freezes_a_new_version()
    {
        var version1 = Version1();
        var (editor, store, frozen) = Create(version1);

        Assert.False(editor.IsEditing);
        Assert.Equal(["RQ-01"], editor.Drafts.Select(d => d.Id));
        Assert.False(editor.FreezeCommand.CanExecute(null));
        Assert.False(editor.AddRequirementCommand.CanExecute(null));

        editor.EditCommand.Execute(null);
        editor.Drafts[0].Statement = "Añadir Calc.Multiply(a, b) y Calc.Divide(a, b)";

        Assert.True(editor.HasPendingChanges);
        Assert.Same(version1, editor.Frozen);
        Assert.Equal("Añadir Calc.Multiply(a, b)", version1.Requirements[0].Statement);
        Assert.Empty(store.Saved);

        await editor.FreezeCommand.ExecuteAsync(null);

        var version2 = Assert.Single(store.Saved);
        Assert.NotEqual(version1.ContentHash, version2.ContentHash);
        Assert.Equal("Añadir Calc.Multiply(a, b) y Calc.Divide(a, b)", version2.Requirements[0].Statement);
        Assert.Same(version2, editor.Frozen);
        Assert.Equal([version2], frozen);
        Assert.False(editor.HasPendingChanges);
    }

    [Fact]
    public void Discarding_goes_back_to_the_frozen_version()
    {
        var (editor, _, _) = Create(Version1());
        editor.EditCommand.Execute(null);
        editor.Drafts[0].Statement = "Otra cosa";
        editor.AddRequirementCommand.Execute(null);

        editor.DiscardCommand.Execute(null);

        Assert.False(editor.IsEditing);
        Assert.False(editor.HasPendingChanges);
        Assert.Equal("Añadir Calc.Multiply(a, b)", Assert.Single(editor.Drafts).Statement);
    }

    [Fact]
    public void Reloading_the_same_version_keeps_a_draft_with_changes_but_a_new_version_replaces_it()
    {
        var version1 = Version1();
        var (editor, _, _) = Create(version1);
        editor.EditCommand.Execute(null);
        editor.Drafts[0].Statement = "Texto a medio escribir";

        editor.Load(Project, version1);

        Assert.True(editor.IsEditing);
        Assert.Equal("Texto a medio escribir", editor.Drafts[0].Statement);

        var version2 = FrozenRequirementSet.Freeze([version1.Requirements[0] with { Statement = "Congelado en otra parte" }], T0);
        editor.Load(Project, version2);

        Assert.False(editor.IsEditing);
        Assert.Equal("Congelado en otra parte", Assert.Single(editor.Drafts).Statement);
    }

    [Fact]
    public async Task Freezing_the_current_content_again_stores_nothing()
    {
        var (editor, store, frozen) = Create(Version1());
        editor.EditCommand.Execute(null);

        Assert.False(editor.HasPendingChanges);
        await editor.FreezeCommand.ExecuteAsync(null);

        Assert.Empty(store.Saved);
        Assert.Empty(frozen);
        Assert.False(editor.IsEditing);
        Assert.Equal("Sin cambios: siguen congelados los mismos requisitos.", editor.Notice);
    }

    [Fact]
    public async Task The_content_of_an_earlier_version_cannot_become_current_again()
    {
        var version1 = Version1();
        var version2 = FrozenRequirementSet.Freeze([version1.Requirements[0] with { Statement = "Añadir Calc.Divide(a, b)" }], T0);
        var (editor, store, frozen) = Create(version1, version2);
        editor.EditCommand.Execute(null);
        editor.Drafts[0].Statement = "Añadir Calc.Multiply(a, b)";

        await editor.FreezeCommand.ExecuteAsync(null);

        Assert.Empty(store.Saved);
        Assert.Empty(frozen);
        Assert.True(editor.IsEditing);
        Assert.Same(version2, editor.Frozen);
        Assert.StartsWith("Ese contenido es el de una versión anterior", editor.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_failure_is_shown_and_the_draft_is_kept()
    {
        var (editor, store, frozen) = Create();
        store.SaveFailure = new IOException("El disco está lleno.");
        TypeVersion1(editor);

        await editor.FreezeCommand.ExecuteAsync(null);

        Assert.Equal("No se pudieron congelar los requisitos: El disco está lleno.", editor.ErrorMessage);
        Assert.True(editor.IsEditing);
        Assert.Single(editor.Drafts);
        Assert.Null(editor.Frozen);
        Assert.Empty(frozen);
    }

    [Fact]
    public void Without_a_project_nothing_is_frozen()
    {
        var store = new FakeRequirementStore();
        var editor = new RequirementsEditorViewModel(new OrchestrationSession(OrchestrationBackend.None with { Requirements = store }), new FakeTimeProvider(T0));
        editor.Load(null, null);
        TypeVersion1(editor);

        Assert.False(editor.FreezeCommand.CanExecute(null));
        Assert.Equal("Abre el proyecto para congelar sus requisitos.", editor.StatusText);
    }
}
