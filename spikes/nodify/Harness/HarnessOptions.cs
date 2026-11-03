using System.IO;

namespace NodifySpike.Harness;

/// <summary>Command line of the scripted run: <c>--auto --out &lt;dir&gt; --results &lt;file&gt; [--only theme,small,input,bench]</c>.</summary>
internal sealed record HarnessOptions(bool Auto, string OutDirectory, string ResultsPath, string Only)
{
    public static HarnessOptions Parse(IReadOnlyList<string> args)
    {
        string Value(string name, string fallback)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        var outDirectory = Path.GetFullPath(Value("--out", Directory.GetCurrentDirectory()));
        var results = Path.GetFullPath(Value("--results", Path.Combine(outDirectory, "spike-results.json")));
        return new HarnessOptions(args.Contains("--auto"), outDirectory, results, Value("--only", "all"));
    }

    public bool Wants(string stage) => Only == "all" || Only.Split(',').Contains(stage);
}
