using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentStudio.GraphWorker;

/// <summary>One <c>using</c> of a file: what it names, and how (a plain one names a namespace; a static one or an alias names a type or a namespace).</summary>
internal sealed record UsingDirective(string Name, bool IsStatic, bool IsAlias);

/// <summary>A namespace a file declares, with the namespaces enclosing it (outermost first, for resolving a <c>using</c> inside it) and the usings that apply to it.</summary>
internal sealed record DeclaredNamespace(string Name, IReadOnlyList<UsingDirective> Usings);

/// <summary>What the syntax of one file says: the namespaces it declares and, for each, the usings in effect there.</summary>
internal sealed record FileDeclarations(IReadOnlyList<DeclaredNamespace> Namespaces);

/// <summary>Why a file could not be scanned. The text of the exception is never kept: it may quote the file.</summary>
internal enum ScanFailure
{
    None,
    TooDeep,
    TimedOut,
    Failed,
}

/// <summary>
/// Reads the syntax of one C# file with the parser of Roslyn and nothing else (docs/20 D-17, F6-12): no <c>Compilation</c>, no analyzers, no generators, no
/// references. Only the <c>using</c> directives of the unit and of each namespace and the declarations of namespaces are looked at, going down through
/// <c>Members</c> only for namespaces: it never enters a type, a body or an expression, so the walk is as deep as the nesting of namespaces, cut at
/// <see cref="MaxNamespaceDepth"/>.
/// </summary>
internal static class CSharpSyntaxScanner
{
    /// <summary>The deepest nesting of namespaces that is read.</summary>
    public const int MaxNamespaceDepth = 64;

    /// <summary>The longest a file may take to parse.</summary>
    public static readonly TimeSpan MaxParseTime = TimeSpan.FromSeconds(5);

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);

    /// <summary>
    /// The deepest nesting of brackets a file may have before it is parsed. The parser of Roslyn recurses into every level and a file nested a few thousand
    /// deep exhausts the stack of the process, which cannot be caught: this cheap count (it knows nothing of strings and comments) turns away the usual
    /// ones, and the host relaunches the process, skipping the file, for what it lets through (docs/20 D-18).
    /// </summary>
    public const int MaxBracketDepth = 256;

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

    public static ScanFailure TryScan(string text, TimeSpan timeout, out FileDeclarations declarations)
    {
        declarations = new FileDeclarations([]);
        if (BracketDepth(text) > MaxBracketDepth)
        {
            return ScanFailure.TooDeep;
        }

        using var cancellation = new CancellationTokenSource(timeout);
        CompilationUnitSyntax root;
        try
        {
            var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: string.Empty, cancellationToken: cancellation.Token);
            root = tree.GetCompilationUnitRoot(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return ScanFailure.TimedOut;
        }
        catch (InsufficientExecutionStackException)
        {
            return ScanFailure.TooDeep;
        }
#pragma warning disable CA1031 // The parser of a hostile file may fail in ways of its own; the file is skipped and the type of the failure is all that is said.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ScanFailure.Failed;
        }

        var found = new List<DeclaredNamespace>();
        var unitUsings = root.Usings.Select(ToUsing).Where(u => u is not null).Select(u => u!).ToList();

        // Members of the unit: namespaces (block or file-scoped). A file-scoped namespace holds the rest of the file as its members.
        var stack = new Stack<(MemberDeclarationSyntax Member, string Parent, IReadOnlyList<UsingDirective> Usings, int Depth)>();
        foreach (var member in root.Members.Reverse())
        {
            stack.Push((member, string.Empty, unitUsings, 1));
        }

        while (stack.Count > 0)
        {
            var (member, parent, inherited, depth) = stack.Pop();
            if (member is not BaseNamespaceDeclarationSyntax declaration)
            {
                continue;
            }

            if (depth > MaxNamespaceDepth)
            {
                return ScanFailure.TooDeep;
            }

            var own = declaration.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
            var name = parent.Length == 0 ? own : parent + "." + own;
            var usings = new List<UsingDirective>(inherited);
            usings.AddRange(declaration.Usings.Select(ToUsing).Where(u => u is not null).Select(u => u!));
            found.Add(new DeclaredNamespace(name, usings));
            foreach (var child in declaration.Members.Reverse())
            {
                stack.Push((child, name, usings, depth + 1));
            }
        }

        declarations = new FileDeclarations(found);
        return ScanFailure.None;
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
