using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// V34 (docs/18 §3, docs/12 §10): A1-bis named some items of the lists, and the walk of 2026-11-03 still found 11 types that
/// UI Automation read by their type name or by the text of their record. A list item that is not a control is named by
/// its <c>ToString()</c>, so every type the screens show as an item of a list gives one of its own. This guard finds those
/// types by reflection, so a new one cannot come back without its name:
/// <list type="bullet">
/// <item>the app's own types: the element types of the public collections, instance or static, of every type of the app,
/// and the types whose name says they are rows of a list;</item>
/// <item>the types of the modules (Domain, Application, Orchestration…) that a public collection of a type of the app holds:
/// the screens may bind one as a list, so each gives its name or says here why it is never one.</item>
/// </list>
/// What it cannot find: a list bound to a property that is not a typed collection (an <c>object</c>), or to a collection of
/// a module type reached through a type of a module and not of the app. Whether a collection found is bound to a list is
/// not known by reflection either: so every type found must give its name, or be listed with why it is never on screen.
/// </summary>
public sealed class ListItemTypesGuardTests
{
    /// <summary>Suffixes of the names of the types made to be rows of a list.</summary>
    private static readonly string[] ItemSuffixes = ["Row", "Entry", "Option", "Item", "Line"];

    /// <summary>
    /// Types the rules find that are never an item of a list on screen, by full name, each with why. Nothing else may be
    /// left without a name.
    /// </summary>
    private static readonly Dictionary<string, string> NeverOnScreen = new(StringComparer.Ordinal)
    {
        ["AgentStudio.Desktop.Markdown.MarkdownListItem"] =
            "The parse of a line of the journal: the converter builds a FlowDocument from it, and the viewer shows its text, never the object.",
        ["AgentStudio.Desktop.Markdown.MarkdownSpan"] =
            "The parse of a piece of a line of the journal: drawn as a Run of the FlowDocument, never as an item.",
        ["AgentStudio.Desktop.Debate.ContributionClaim"] =
            "The parse of a contribution: the room lists its claims as text (DebateContributionViewModel.Claims), never the object.",
        ["AgentStudio.Desktop.Debate.ContributionQuote"] =
            "The parse of a contribution: the room lists its quotes as text (DebateContributionViewModel.Quotes), never the object.",
        ["AgentStudio.Desktop.Orchestrator.Canvas.FileCanvasLayoutStore+DraftTaskJson"] =
            "The JSON of a task of the saved draft of the canvas: written to and read from the file, never shown.",
        ["AgentStudio.Desktop.Orchestrator.Canvas.FileCanvasLayoutStore+DraftDependencyJson"] =
            "The JSON of a wire of the saved draft of the canvas: written to and read from the file, never shown.",
        ["AgentStudio.Desktop.Background.TrayMenuItem"] =
            "An entry of the menu of the tray icon: Windows Forms builds a ToolStripMenuItem with its Text, never a WPF list item.",
        ["AgentStudio.Desktop.Lock.WaitingQuestion"] =
            "A question waiting for the unlock: the lock screen lists it as text (LockScreenViewModel.WaitingItems), never the object.",

        // Types of the modules held by collections of the app: data the screens turn into rows of their own.
        ["AgentStudio.Application.Coordination.DebateParticipantView"] =
            "The participants of a verdict card (VerdictCardContext.Participants): the card shows them as LabelRow.",
        ["AgentStudio.Domain.Agents.AgentDefinition"] =
            "The roster a node editor or a session holds (EditorOptions.Roster, SessionContext.Roster): the lists show AgentChoice and AgentMemoryViewModel.",
        ["AgentStudio.Domain.Judging.JudgeIdentity"] =
            "The judges of a debate room (DebateRoomContext.Judges): the room shows them in its texts, never as a list of these.",
        ["AgentStudio.Domain.Orchestration.DependencySpec"] =
            "The dependencies of a node that are not wires (NodeViewModel.ExtraDependencies): kept for the plan, never listed.",
        ["AgentStudio.Domain.Orchestration.PlanViolation"] =
            "The violations of the plan (OrchestratorDocumentViewModel.Violations): the screen lists them as PlanProblem.",
        ["AgentStudio.Domain.Orchestration.TaskSpec"] =
            "The tasks of a draft or of the state of the canvas (PlanDraft.Tasks, CanvasState.Tasks): the canvas shows NodeViewModel.",
        ["AgentStudio.Domain.Requirements.Requirement"] =
            "The requirements a node editor holds (EditorOptions.Requirements): the editor lists CheckOption.",
        ["AgentStudio.Orchestration.Engine.ScheduledResume"] =
            "What a pass of the consented resume did (ConsentedPass.Outcome): said in the status message, never listed.",
        ["AgentStudio.Application.Decisions.ContextPartSnapshot"] =
            "The parts of a prompt in the live state (LiveContext.Parts, Phase 13): a bar shows them as MetricBarSegment, named by its label and value, never the record.",
    };

    [Fact]
    public void Every_type_shown_as_an_item_of_a_list_is_named_by_its_own_to_string()
    {
        var found = ListItemTypes();
        var unnamed = found
            .Where(t => !NeverOnScreen.ContainsKey(t.FullName!) && !NamedByItsToString(t))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unnamed.Count == 0, "UI Automation would read these by their type or by the text of their record; give each a ToString() of its own, or say here why it is never on screen: " + string.Join(", ", unnamed));

        // The rules find the 11 types of the walk, types of the modules too, and every exception is still a type the rules
        // find (none is stale).
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ActivityEntry", "EvidenceEntry", "AssessmentRow", "MonitorTaskViewModel", "AgentMemoryViewModel", "RequirementDraftViewModel",
                "ConnectorViewModel", "VerdictPanelViewModel", "DimensionOption", "UsageGroupRow", "MemoryEntryViewModel", "ShellInfo",
            },
            found.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));
        Assert.Subset(found.Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal), NeverOnScreen.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The guard tells a <c>ToString()</c> of its own from the one of <see cref="object"/> and the one a record synthesizes.</summary>
    [Fact]
    public void The_guard_tells_a_name_of_its_own_from_the_inherited_and_the_synthesized_ones()
    {
        Assert.False(NamedByItsToString(typeof(PlainClass)));
        Assert.False(NamedByItsToString(typeof(PlainRecord)));
        Assert.True(NamedByItsToString(typeof(NamedRecord)));
        Assert.True(NamedByItsToString(typeof(NamedClass)));
        Assert.StartsWith("PlainRecord {", new PlainRecord("haiku").ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The types shown as items of a list: the element types of the public collections (instance or static) of every type of
    /// the app, of the app or of a module of Agent Studio, and the app's types whose name says they are rows of a list.
    /// </summary>
    private static List<Type> ListItemTypes()
    {
        var desktop = typeof(MainViewModel).Assembly;
        Type[] types;
        try
        {
            types = desktop.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        var found = new HashSet<Type>();
        foreach (var type in types.Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), false)))
        {
            if ((type.IsClass || type.IsValueType) && !type.IsEnum && !type.IsAbstract && ItemSuffixes.Any(s => type.Name.Split('`')[0].EndsWith(s, StringComparison.Ordinal)))
            {
                found.Add(type);
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (ElementType(property.PropertyType) is { } element && IsOfAgentStudio(element) && !element.IsEnum && !element.IsInterface && !element.IsPrimitive)
                {
                    found.Add(element.IsGenericType ? element.GetGenericTypeDefinition() : element);
                }
            }
        }

        return [.. found];
    }

    /// <summary>A type of the app or of one of the modules of Agent Studio, not of .NET or of a package.</summary>
    private static bool IsOfAgentStudio(Type type) =>
        type.Assembly.GetName().Name is { } name && (name == "AgentStudio.Desktop" || name.StartsWith("AgentStudio.", StringComparison.Ordinal));

    /// <summary>The <c>T</c> of a collection of <c>T</c>; null for a text or anything that is not a collection.</summary>
    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    /// <summary>The type gives a <c>ToString()</c> of its own: not the one of <see cref="object"/>, nor the one a record synthesizes.</summary>
    private static bool NamedByItsToString(Type type)
    {
        var method = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        return method.DeclaringType != typeof(object) && method.DeclaringType != typeof(ValueType)
            && !method.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }

    private sealed class PlainClass;

    private sealed record PlainRecord(string Label);

    private sealed record NamedRecord(string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class NamedClass
    {
        public override string ToString() => "nombre";
    }
}
