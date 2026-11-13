using System.Text.RegularExpressions;

namespace AgentStudio.Infrastructure.Hosting.Cli;

/// <summary>The methods the adapter uses, a closed list (docs/21 D-10).</summary>
internal enum GhMethod
{
    Get,
    Post,
    Patch,
    Put,
    Delete,
}

/// <summary>
/// One call to <c>gh api</c>. <paramref name="Path"/> is a full path built by <c>GitHubApiPath</c> (the runner checks it again); <paramref name="IfNoneMatch"/> is an <c>ETag</c> the remote gave
/// (the only datum of the remote that becomes an argument, besides the validated path); <paramref name="Body"/> is JSON that travels through standard input, never as an argument.
/// </summary>
internal sealed record GhCall(GhMethod Method, string Path, string? IfNoneMatch = null, string? Body = null);

/// <summary>
/// The one form of the command line of <c>gh</c> (docs/21 D-10): <c>gh api --hostname github.com -X METHOD -i -H … -H … [-H "If-None-Match: …"] [--input -] PATH</c>. Never <c>-f</c>,
/// <c>-F</c>, <c>--jq</c>, <c>--template</c>, <c>--paginate</c>, <c>--cache</c> or <c>--verbose</c>; never a brace in any argument (<c>gh</c> would fill <c>{owner}</c>, <c>{repo}</c> and <c>{branch}</c>
/// from the folder it runs in).
/// </summary>
internal static partial class GhArguments
{
    internal const string Host = "github.com";

    /// <summary>The version of the API, fixed: the adapter depends on it more than on the version of <c>gh</c> (D-14).</summary>
    internal const string ApiVersion = "2022-11-28";

    internal const string AcceptHeader = "Accept: application/vnd.github+json";

    internal const string ApiVersionHeader = "X-GitHub-Api-Version: " + ApiVersion;

    internal const string IfNoneMatchHeader = "If-None-Match: ";

    internal const int MaxPathLength = 2048;

    // /user, or /repos/<owner>/<name> and segments of unreserved characters and %XX, and a query of names, = and values of the same kind.
    [GeneratedRegex(@"^/(?:user|repos/[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}(?:/[A-Za-z0-9._~%-]{1,300})*)(?:\?[A-Za-z0-9._~%=&-]{1,1500})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPath();

    // W/ optional, and between double quotes 1 to 128 visible ASCII characters that are not a quote or a space (docs/21 D-10).
    [GeneratedRegex("^(?:W/)?\"[\\x21\\x23-\\x7E]{1,128}\"\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ETagShape();

    internal static string MethodName(GhMethod method) => method switch
    {
        GhMethod.Get => "GET",
        GhMethod.Post => "POST",
        GhMethod.Patch => "PATCH",
        GhMethod.Put => "PUT",
        GhMethod.Delete => "DELETE",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown method."),
    };

    /// <summary>Whether <paramref name="path"/> is one of the two forms of path the adapter builds, with no <c>.</c> or <c>..</c> segment (not even percent-encoded) and no brace.</summary>
    internal static bool IsAllowedPath(string? path)
    {
        if (path is null || path.Length > MaxPathLength || !AllowedPath().IsMatch(path))
        {
            return false;
        }

        var query = path.IndexOf('?', StringComparison.Ordinal);
        var segments = (query < 0 ? path : path[..query]).Split('/');
        return !segments.Any(s => Uri.UnescapeDataString(s) is "." or "..");
    }

    /// <summary>Whether <paramref name="etag"/> has the exact form of an <c>ETag</c>. What does not is dropped and the request goes without condition.</summary>
    internal static bool IsETag(string? etag) => etag is not null && ETagShape().IsMatch(etag);

    /// <exception cref="ArgumentException">The path is not one the adapter builds, the <c>ETag</c> has not its form, or a body goes with a method that sends none.</exception>
    internal static IReadOnlyList<string> Build(GhCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!IsAllowedPath(call.Path))
        {
            throw new ArgumentException("The path of a call to gh is a full path built by the adapter.", nameof(call));
        }

        if (call.IfNoneMatch is not null && !IsETag(call.IfNoneMatch))
        {
            throw new ArgumentException("An If-None-Match value has the exact form of an ETag.", nameof(call));
        }

        if (call.Body is not null && call.Method is GhMethod.Get or GhMethod.Delete)
        {
            throw new ArgumentException("GET and DELETE send no body.", nameof(call));
        }

        var arguments = new List<string>
        {
            "api", "--hostname", Host, "-X", MethodName(call.Method), "-i", "-H", AcceptHeader, "-H", ApiVersionHeader,
        };
        if (call.IfNoneMatch is not null)
        {
            arguments.Add("-H");
            arguments.Add(IfNoneMatchHeader + call.IfNoneMatch);
        }

        if (call.Body is not null)
        {
            arguments.Add("--input");
            arguments.Add("-");
        }

        arguments.Add(call.Path);
        return arguments;
    }
}
