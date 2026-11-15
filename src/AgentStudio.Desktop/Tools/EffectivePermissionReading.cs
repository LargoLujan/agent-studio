using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Desktop.Tools;

/// <summary>A declared permission and its limits, read from the launch preview or the trusted verification profile.</summary>
public sealed record EffectivePermissionReading(string Title, string Files, string Network, string OutboundData = "")
{
    public string AccessibleName => OutboundData.Length == 0 ? $"{Title}. {Files} {Network}" : $"{Title}. {Files} {Network} {OutboundData}";

    public override string ToString() => AccessibleName;

    /// <summary>No probe runs here: a preview must never claim that the installed CLI was measured.</summary>
    public static IReadOnlyList<EffectivePermissionReading> Read(
        IReadOnlyList<AgentDefinition> roster,
        IReadOnlyList<AgentToolSet> launches,
        VerificationProfile? profile,
        IUiTextProvider texts)
    {
        var readings = new List<EffectivePermissionReading>();
        foreach (var launch in launches)
        {
            var agent = roster.First(a => a.Id == launch.Agent);
            var title = texts.Format("Tools_Permissions_Agent", agent.Name, $"{agent.Model.Vendor.Value}/{launch.Provider.Value}",
                agent.Model.Model ?? texts.Get("Tools_Permissions_DefaultModel"),
                texts.Get(launch.WritesFiles ? "Tools_Permissions_Write" : "Tools_Permissions_Read"));
            // Provider communication is separate from the network tools available to the agent.
            var outbound = texts.Format("Tools_Permissions_AgentData", agent.Model.Vendor.Value);
            if (launch.Provider == ProviderId.ClaudeCode)
            {
                // Bash can reach the network even without WebFetch/WebSearch. Tool restrictions are not an OS firewall.
                var hasNetworkTool = launch.Available.Any(tool => tool is "Bash" or "WebFetch" or "WebSearch");
                readings.Add(new(title, texts.Get("Tools_Permissions_ClaudeFiles"),
                    texts.Get(hasNetworkTool ? "Tools_Permissions_ClaudeNetwork" : "Tools_Permissions_ClaudeNoNetworkTools"), outbound));
            }
            else if (launch.Provider == ProviderId.CodexCli)
            {
                var files = launch.Sandbox is { } sandbox
                    ? texts.Format("Tools_Permissions_CodexFiles", sandbox)
                    : texts.Get("Tools_Permissions_Unknown");
                var networkKey = launch.Network switch
                {
                    true => "Tools_Permissions_CodexNetwork",
                    false => "Tools_Permissions_CodexNoNetwork",
                    null => "Tools_Permissions_UnknownNetwork",
                };
                readings.Add(new(title, files, texts.Get(networkKey), outbound));
            }
            else
            {
                readings.Add(new(title, texts.Get("Tools_Permissions_Unknown"), texts.Get("Tools_Permissions_UnknownNetwork"), outbound));
            }
        }

        foreach (var step in profile?.Steps ?? [])
        {
            // Only the isolation the runner actually implements may be labelled Low. Unknown values fail closed in the view.
            var low = step.Isolation == ProcessIsolation.LowIntegrity;
            readings.Add(new(texts.Format("Tools_Permissions_Step", step.Id, step.Title),
                texts.Get(low ? "Tools_Permissions_LowFiles" : "Tools_Permissions_NoIsolation"),
                texts.Get("Tools_Permissions_StepNetwork"), texts.Get("Tools_Permissions_StepData")));
        }

        return readings;
    }
}
