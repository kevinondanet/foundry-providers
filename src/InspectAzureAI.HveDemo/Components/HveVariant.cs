using System.Text.RegularExpressions;

namespace InspectAzureAI.HveDemo.Components;

/// <summary>
/// The agent runtime a run uses: the GitHub Copilot CLI inside the sandbox (bridged to the model), or Inspect's own
/// generic agent loop (<c>basic_agent</c> with the sandbox <c>bash</c> tool and <c>submit</c>), with no external CLI.
/// </summary>
public static class HveHarness
{
    public const string Copilot = "copilot";

    public const string Generic = "generic";

    public static readonly IReadOnlyList<string> Names = [Copilot, Generic];
}

/// <summary>
/// The engineering framework layered on the harness: the vendored HVE Core plugin (agents, skills, prompts,
/// instructions) provisioned into the sandbox and briefed to the agent, or nothing at all.
/// </summary>
public static class HveFramework
{
    public const string Hve = "hve";

    public const string None = "none";

    public static readonly IReadOnlyList<string> Names = [Hve, None];
}

/// <summary>
/// One cell of the harness x framework matrix. The two axes are orthogonal the way <c>--model</c> is orthogonal to both:
/// <c>copilot+hve</c> is the original demo, <c>generic+none</c> the original baseline, and the other two cells isolate
/// what the CLI contributes from what the plugin content contributes.
/// </summary>
public sealed record HveVariant(string Harness, string Framework)
{
    /// <summary>The deprecated <c>--solver</c> spellings and the cells they stand for.</summary>
    public const string CopilotAlias = "copilot";

    public const string BasicAlias = "basic";

    public static readonly IReadOnlyList<string> Aliases = [CopilotAlias, BasicAlias];

    /// <summary>What every command and registered task ran before the split: the Copilot CLI with the HVE plugin.</summary>
    public static readonly HveVariant Default = new(HveHarness.Copilot, HveFramework.Hve);

    /// <summary>Whether the plugin is provisioned and briefed (and the <c>hve_artefact_used</c> scorer runs).</summary>
    public bool UsesFramework => Framework == HveFramework.Hve;

    public bool IsCopilot => Harness == HveHarness.Copilot;

    /// <summary><c>&lt;harness&gt;+&lt;framework&gt;</c>, the label task metadata and the console use.</summary>
    public string Label => $"{Harness}+{Framework}";

    /// <summary>
    /// Validates and lower-cases the two names. The messages are what <c>Program</c> prints for a bad flag, verbatim, so
    /// they carry no parameter name (<c>ArgumentException</c> would append <c>(Parameter 'harness')</c> to it).
    /// </summary>
    public static HveVariant Parse(string harness, string framework)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(framework);
        var h = harness.ToLowerInvariant();
        var f = framework.ToLowerInvariant();
        if (!HveHarness.Names.Contains(h, StringComparer.Ordinal))
        {
            throw new ArgumentException($"--harness expects {string.Join(" or ", HveHarness.Names)}");
        }

        if (!HveFramework.Names.Contains(f, StringComparer.Ordinal))
        {
            throw new ArgumentException($"--framework expects {string.Join(" or ", HveFramework.Names)}");
        }

        return new HveVariant(h, f);
    }

    /// <summary>The cell a deprecated <c>--solver</c> value names: <c>copilot</c> is copilot+hve, <c>basic</c> is generic+none.</summary>
    public static HveVariant FromSolverAlias(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        return alias.ToLowerInvariant() switch
        {
            CopilotAlias => Default,
            BasicAlias => new HveVariant(HveHarness.Generic, HveFramework.None),
            _ => throw new ArgumentException($"--solver expects {string.Join(" or ", Aliases)} (deprecated: use --harness and --framework)"),
        };
    }
}

/// <summary>
/// The literal markers shared by the briefings (<c>HveSolvers</c>), the scripted model (<c>FakeHveModel</c>, which reads
/// them to tell the four cells apart and to find the plugin) and the evidence scorer (<c>HveScorers</c>). Changing one
/// here changes it for all three.
/// </summary>
public static partial class HveBriefing
{
    /// <summary>Present in both HVE briefings and in neither plain briefing.</summary>
    public const string FrameworkMarker = "plugin `hve-core`";

    /// <summary>The generic HVE briefing carries one line <c>HVE plugin directory: &lt;path&gt;</c>; the scripted model reads the path from it.</summary>
    public const string PluginDirectoryPrefix = "HVE plugin directory: ";

    /// <summary>The block the generic HVE briefing embeds a selected agent's body in (the Copilot CLI's own element name).</summary>
    public const string AgentInstructionsOpen = "<agent_instructions>";

    public const string AgentInstructionsClose = "</agent_instructions>";

    /// <summary>When the host cannot read the agent file, the briefing tells the agent to read it: this prefix, then <c>hve-core:&lt;id&gt;</c>.</summary>
    public const string AgentFallbackPrefix = "The task is to be carried out as the custom agent ";

    /// <summary>The plugin directory named by a briefing (system messages or the prefixed first prompt), or null. The path may contain spaces.</summary>
    public static string? PluginDirectoryIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PluginDirectoryLine().Match(text) is { Success: true } m ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^HVE plugin directory: (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex PluginDirectoryLine();
}
