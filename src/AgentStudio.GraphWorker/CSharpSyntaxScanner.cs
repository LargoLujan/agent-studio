using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentStudio.GraphWorker;

/// <summary>One <c>using</c> of a file: what it names, and how (a plain one names a namespace; a static one or an alias names a type or a namespace).</summary>
internal sealed record UsingDirective(string Name, bool IsStatic, bool IsAlias);

/// <summary>
/// The usings in effect at a place of a file: the ones of its own level and, by reference, those of the level above. Sharing the level above (never copying it)
/// keeps a file with thousands of sibling namespaces from multiplying its usings.
/// </summary>
internal sealed record UsingScope(IReadOnlyList<UsingDirective> Own, UsingScope? Outer)
{
    /// <summary>Every directive in effect, the nearest level first.</summary>
    public IEnumerable<UsingDirective> All()
    {
        for (var scope = this; scope is not null; scope = scope.Outer)
        {
            foreach (var directive in scope.Own)
            {
                yield return directive;
            }
        }
    }
}

/// <summary>A namespace a file declares, with the usings that apply to it.</summary>
internal sealed record DeclaredNamespace(string Name, UsingScope Scope);

/// <summary>What the syntax of one file says; <paramref name="Truncated"/> when a cap of the file (namespaces, usings) cut what was read.</summary>
internal sealed record FileDeclarations(IReadOnlyList<DeclaredNamespace> Namespaces, bool Truncated = false);

/// <summary>Why a file could not be scanned. The text of the exception is never kept: it may quote the file.</summary>
internal enum ScanFailure
{
    None,
    TooDeep,
    TimedOut,
    Failed,

    /// <summary>The parser ignored the cancellation and did not end in its time plus a grace: the process must end itself (the host skips the file).</summary>
    Stuck,
}

/// <summary>How a file is scanned: the time, the stack of the thread and the caps of a file.</summary>
internal sealed record ScanOptions
{
    /// <summary>The stack of the thread that parses. Real code nests a few hundred levels; a file made to exhaust it still can, and the host relaunches the process then.</summary>
    public const int DefaultStackBytes = 64 * 1024 * 1024;

    /// <summary>A variable only a test sets, in KB: a small stack makes the files that exhaust it small too.</summary>
    public const string StackVariable = "AGENTSTUDIO_GRAPHWORKER_PARSE_STACK_KB";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan StuckGrace { get; init; } = TimeSpan.FromSeconds(5);

    public int StackBytes { get; init; } = FromEnvironment();

    public int MaxNamespaces { get; init; } = 1_000;

    public int MaxUsings { get; init; } = 1_000;

    /// <summary>Runs on the parser thread before the parser, with the cancellation of the time limit: a test blocks here, with or without honoring it, to stand for a parser that is slow or deaf.</summary>
    public Action<string, CancellationToken>? BeforeParse { get; init; }

    public static ScanOptions Default { get; } = new();

    private static int FromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable(StackVariable), out var kb) && kb is >= 64 and <= 1_048_576 ? kb * 1024 : DefaultStackBytes;
}

/// <summary>
/// Reads the syntax of one C# file with the parser of Roslyn and nothing else (docs/20 D-17, F6-12): no <c>Compilation</c>, no analyzers, no generators, no
/// references. Only the <c>using</c> directives of the unit and of each namespace and the declarations of namespaces are looked at, going down through
/// <c>Members</c> only for namespaces. The parser runs on a thread with a large stack of its own and a time limit; a parser that does not return in time
/// is <see cref="ScanFailure.Stuck"/>. A cheap count turns away what is plainly made to exhaust the stack, and the host relaunches the process for what is not.
/// </summary>
internal static class CSharpSyntaxScanner
{
    /// <summary>The deepest nesting of namespaces that is read.</summary>
    public const int MaxNamespaceDepth = 64;

    /// <summary>The longest a file may take to parse.</summary>
    public static readonly TimeSpan MaxParseTime = TimeSpan.FromSeconds(5);

    /// <summary>The deepest nesting of brackets a file may have before it is parsed.</summary>
    public const int MaxBracketDepth = 256;

    /// <summary>The longest run of one operator character (spaces aside) a file may have: <c>!!!!…</c>, <c>----…</c>, <c>~~~~…</c>.</summary>
    public const int MaxOperatorRun = 256;

    /// <summary>The most characters between two of <c>;</c>, <c>{</c> and <c>}</c>: a statement of hundreds of thousands of characters is a nest, not code.</summary>
    public const int MaxStatementCharacters = 400_000;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);

    private const string OperatorCharacters = "!-~+<>(){}[]?=&|*:.,^%";

    /// <summary>The deepest nesting of <c>(</c>, <c>[</c> and <c>{</c> in the text, counted without understanding the language.</summary>
    internal static int BracketDepth(string text)
    {
        var depth = 0;
        var deepest = 0;
        foreach (var c in text)
        {
            if (c is '(' or '[' or '{')
            {
                deepest = Math.Max(deepest, ++depth);
            }
            else if (c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
        }

        return deepest;
    }

    /// <summary>Whether the text is plainly made to exhaust the stack: nested brackets, a long run of one operator, or a statement with no end.</summary>
    internal static bool LooksLikeANest(string text)
    {
        if (BracketDepth(text) > MaxBracketDepth)
        {
            return true;
        }

        var run = 0;
        var previous = '\0';
        var statement = 0;
        foreach (var c in text)
        {
            statement++;
            if (c is ';' or '{' or '}')
            {
                statement = 0;
            }
            else if (statement > MaxStatementCharacters)
            {
                return true;
            }

            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c == previous && OperatorCharacters.Contains(c, StringComparison.Ordinal))
            {
                if (++run > MaxOperatorRun)
                {
                    return true;
                }
            }
            else
            {
                run = 1;
                previous = c;
            }
        }

        return false;
    }

    public static ScanFailure TryScan(string text, TimeSpan timeout, out FileDeclarations declarations) =>
        TryScan(text, ScanOptions.Default with { Timeout = timeout }, out declarations);

    public static ScanFailure TryScan(string text, ScanOptions options, out FileDeclarations declarations)
    {
        declarations = new FileDeclarations([]);
        if (LooksLikeANest(text))
        {
            return ScanFailure.TooDeep;
        }

        var failure = ScanFailure.Failed;
        var found = new FileDeclarations([]);
        var thread = new Thread(() => (failure, found) = Work(text, options), options.StackBytes) { IsBackground = true, Name = "graph-parse" };
        thread.Start();
        if (!thread.Join(options.Timeout + options.StuckGrace))
        {
            return ScanFailure.Stuck;
        }

        declarations = found;
        return failure;
    }

    private static (ScanFailure Failure, FileDeclarations Found) Work(string text, ScanOptions options)
    {
        using var cancellation = new CancellationTokenSource(options.Timeout);
        CompilationUnitSyntax root;
        try
        {
            options.BeforeParse?.Invoke(text, cancellation.Token);
            root = Parse(text, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return (ScanFailure.TimedOut, new FileDeclarations([]));
        }
        catch (InsufficientExecutionStackException)
        {
            return (ScanFailure.TooDeep, new FileDeclarations([]));
        }
#pragma warning disable CA1031 // The parser of a hostile file may fail in ways of its own; the file is skipped and the type of the failure is all that is said.
        catch (Exception)
#pragma warning restore CA1031
        {
            return (ScanFailure.Failed, new FileDeclarations([]));
        }

        var truncated = false;
        var found = new List<DeclaredNamespace>();
        var unit = new UsingScope(Take(root.Usings, options.MaxUsings, ref truncated), null);

        // Members of the unit: namespaces (block or file-scoped). A file-scoped namespace holds the rest of the file as its members.
        var stack = new Stack<(MemberDeclarationSyntax Member, string Parent, UsingScope Scope, int Depth)>();
        foreach (var member in root.Members.Reverse())
        {
            stack.Push((member, string.Empty, unit, 1));
        }

        while (stack.Count > 0)
        {
            var (member, parent, outer, depth) = stack.Pop();
            if (member is not BaseNamespaceDeclarationSyntax declaration)
            {
                continue;
            }

            if (depth > MaxNamespaceDepth)
            {
                return (ScanFailure.TooDeep, new FileDeclarations([]));
            }

            if (found.Count >= options.MaxNamespaces)
            {
                truncated = true;
                break;
            }

            var own = declaration.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
            var name = parent.Length == 0 ? own : parent + "." + own;
            var scope = new UsingScope(Take(declaration.Usings, options.MaxUsings, ref truncated), outer);
            found.Add(new DeclaredNamespace(name, scope));
            foreach (var child in declaration.Members.Reverse())
            {
                stack.Push((child, name, scope, depth + 1));
            }
        }

        return (ScanFailure.None, new FileDeclarations(found, truncated));
    }

    private static CompilationUnitSyntax Parse(string text, CancellationToken ct) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions, path: string.Empty, cancellationToken: ct).GetCompilationUnitRoot(ct);

    private static List<UsingDirective> Take(SyntaxList<UsingDirectiveSyntax> directives, int max, ref bool truncated)
    {
        var taken = new List<UsingDirective>();
        foreach (var directive in directives)
        {
            if (taken.Count >= max)
            {
                truncated = true;
                break;
            }

            if (ToUsing(directive) is { } converted)
            {
                taken.Add(converted);
            }
        }

        return taken;
    }

    private static UsingDirective? ToUsing(UsingDirectiveSyntax directive)
    {
        if (directive.Name is null)
        {
            return null;
        }

        return new UsingDirective(
            directive.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal),
            directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword),
            directive.Alias is not null);
    }
}
