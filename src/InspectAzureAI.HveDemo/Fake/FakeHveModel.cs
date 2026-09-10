using System.Globalization;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.HveDemo.Fake;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> that plays a diligent HVE-trained engineer without
/// any network access, the way <c>FakeCtfModel</c> does for the CTF sample. Every turn is computed from the conversation
/// so far, so the run is deterministic and still exercises the real pipeline, and it plays all four cells of the
/// harness x framework matrix (<see cref="HveVariant"/>), telling them apart from what the conversation shows rather
/// than from a flag: a <c>submit</c> tool on offer means the generic <c>basic_agent</c> loop, otherwise the Copilot CLI
/// (real or fake) is calling through the bridge; the framework is <c>hve</c> when the briefing (the system messages, or the
/// first prompt the CLI prepends them to) carries <see cref="HveBriefing.FrameworkMarker"/>, and the generic briefing's
/// <c>HVE plugin directory:</c> line says where the plugin sits in the sandbox. Each cell then follows a short plan:
/// through the CLI with the plugin it loads the skill (or prompt command, or reads the instruction file) the sample names,
/// creates the artefact from the reference solution under <c>hve/reference/&lt;id&gt;/</c>, runs the sample's check and
/// reports the file; through the CLI without the plugin it reads the instruction file only (no <c>skill</c> tool, no agent);
/// through the generic loop with the plugin it first <c>cat</c>s the skill, prompt and instruction files the sample names
/// (so the transcript-evidence scorer sees them read), then writes the artefact with a <c>bash</c> heredoc, checks and
/// submits; through the generic loop without the plugin it writes, checks and submits. As the grader of
/// <c>artefact_quality</c> it answers <c>GRADE: C</c>. The sample is recognised from its input text inside the first user message.
///
/// The self-check step is best-effort by design: for the samples whose check is a <c>tests/check_*.py</c> grader the
/// script is not in the workspace (it is restored by the check scorer right before the real check), so the fake's own
/// check fails in the transcript, as a real agent's would; <c>hve_check</c> is unaffected.
/// </summary>
public static class FakeHveModel
{
    public const string ModelName = "hve-scripted";

    private const int TurnBudget = 256;

    private const string HereDoc = "HVE_EOF";

    /// <summary>
    /// The scripted model over <paramref name="dataset"/>'s samples and the reference solutions under
    /// <paramref name="referenceRoot"/>; <paramref name="pluginDirectory"/> is the host copy of the plugin the generic+hve
    /// plan derives its read paths from (the file layout is the same in the sandbox copy).
    /// </summary>
    public static Model Create(IDataset dataset, string referenceRoot, string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(referenceRoot);
        ArgumentNullException.ThrowIfNull(pluginDirectory);
        var samples = dataset.ToList();
        return new Model(new ScriptedModelApi(
            Enumerable.Repeat(ScriptedTurn.From((messages, tools) => Respond(samples, referenceRoot, pluginDirectory, messages, tools)), TurnBudget),
            ModelName));
    }

    /// <summary>The scripted model over the demo's own dataset, references and vendored plugin.</summary>
    public static Model Create() => Create(HveDataset.Load(), HveData.ReferenceRoot, HveData.PluginDirectory);

    /// <summary>The reference artefact of a sample: <c>&lt;referenceRoot&gt;/&lt;id&gt;/&lt;artefact&gt;</c>.</summary>
    public static string ReferencePath(string referenceRoot, Sample sample) =>
        Path.Combine(referenceRoot, Convert.ToString(sample.Id, CultureInfo.InvariantCulture) ?? "", HveDataset.Artefact(sample.Metadata));

    private static ModelOutput Respond(IReadOnlyList<Sample> samples, string referenceRoot, string pluginDirectory, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var lastUser = messages.LastOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        ModelOutput output;
        if (lastUser.Contains("[Criterion]:", StringComparison.Ordinal) && lastUser.Contains("GRADE", StringComparison.Ordinal))
        {
            output = ScriptedTurn.Text("The submission contains the artefact the criterion describes and the agent's summary names it.\n\nGRADE: C").Output!;
            return WithUsage(messages, output);
        }

        var firstUser = messages.FirstOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        var sample = samples.FirstOrDefault(candidate => candidate.Input.Text is { Length: > 0 } input && firstUser.Contains(input, StringComparison.Ordinal));
        if (sample is null)
        {
            output = ScriptedTurn.Text("I do not recognise this task, so I have nothing to do.").Output!;
            return WithUsage(messages, output);
        }

        // The briefing: the system messages under the generic harness; under copilot the agent prepends them to the first
        // prompt and the CLI wraps that in the first user message. Later user messages (skill contexts, "please proceed") are not consulted.
        var toolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var briefing = string.Join("\n", messages.OfType<ChatMessageSystem>().Select(message => message.Text).Append(firstUser));
        var generic = toolNames.Contains(Solvers.BasicAgentSubmitName);
        var hve = briefing.Contains(HveBriefing.FrameworkMarker, StringComparison.Ordinal);
        var plan = (generic, hve) switch
        {
            (true, true) => GenericHvePlan(sample, referenceRoot, pluginDirectory, HveBriefing.PluginDirectoryIn(briefing) ?? HveData.PluginSandboxPath),
            (true, false) => GenericPlainPlan(sample, referenceRoot),
            (false, true) => CopilotHvePlan(sample, referenceRoot, toolNames),
            (false, false) => CopilotPlainPlan(sample, referenceRoot, toolNames),
        };

        // One plan entry per assistant turn; a stray extra turn repeats the final entry (the summary or submit) rather than failing.
        var step = messages.Count(message => message is ChatMessageAssistant);
        output = plan[Math.Min(step, plan.Count - 1)];
        return WithUsage(messages, output);
    }

    /// <summary>copilot+hve: load the named skill (or prompt command, or read the instruction file), create the artefact, run the check, summarise.</summary>
    private static IReadOnlyList<ModelOutput> CopilotHvePlan(Sample sample, string referenceRoot, IReadOnlySet<string> toolNames)
    {
        var components = HveDataset.Components(sample.Metadata);
        var skills = Named(components, "skill");
        var prompts = Named(components, "prompt");
        var instructions = Named(components, "instructions");
        var plan = new List<ModelOutput>();
        if (skills.Count > 0 && toolNames.Contains("skill"))
        {
            plan.Add(ScriptedTurn.ToolCall("skill", new { skill = skills[0] }, text: $"Loading the {skills[0]} skill as my checklist before I write anything.").Output!);
        }
        else if (prompts.Count > 0 && toolNames.Contains("skill"))
        {
            plan.Add(ScriptedTurn.ToolCall("skill", new { skill = prompts[0] + ".prompt" }, text: $"Loading the {prompts[0]} prompt command.").Output!);
        }
        else if (instructions.Count > 0 && toolNames.Contains("view"))
        {
            plan.Add(ViewInstructions(instructions[0]));
        }

        plan.AddRange(CopilotTail(sample, referenceRoot, toolNames));
        return plan;
    }

    /// <summary>copilot+none: no plugin to load, so read the instruction file the sample names (if any), create, check, summarise.</summary>
    private static IReadOnlyList<ModelOutput> CopilotPlainPlan(Sample sample, string referenceRoot, IReadOnlySet<string> toolNames)
    {
        var instructions = Named(HveDataset.Components(sample.Metadata), "instructions");
        var plan = new List<ModelOutput>();
        if (instructions.Count > 0 && toolNames.Contains("view"))
        {
            plan.Add(ViewInstructions(instructions[0]));
        }

        plan.AddRange(CopilotTail(sample, referenceRoot, toolNames));
        return plan;
    }

    /// <summary>The CLI turns after the read: <c>create</c> the artefact, <c>bash</c> the check (when the tool is offered), then the summary text.</summary>
    private static IEnumerable<ModelOutput> CopilotTail(Sample sample, string referenceRoot, IReadOnlySet<string> toolNames)
    {
        var artefact = HveDataset.Artefact(sample.Metadata);
        var check = HveDataset.Check(sample.Metadata);
        var sandboxPath = HveData.SandboxWorkingDirectory + "/" + artefact;
        yield return ScriptedTurn.ToolCall("create", new { path = sandboxPath, file_text = Reference(referenceRoot, sample) }, text: $"Writing {artefact}.").Output!;
        if (check.Length > 0 && toolNames.Contains("bash"))
        {
            // A create'd shell script is not executable; the real model would chmod it too.
            var command = artefact.EndsWith(".sh", StringComparison.Ordinal) ? $"chmod +x '{artefact}' && {check}" : check;
            yield return ScriptedTurn.ToolCall("bash", new { command, description = "verify the artefact" }, text: "Verifying with the task's check.").Output!;
        }

        yield return ScriptedTurn.Text(Summary(artefact, check)).Output!;
    }

    private static ModelOutput ViewInstructions(string name) =>
        ScriptedTurn.ToolCall("view", new { path = $"{HveData.SandboxWorkingDirectory}/.github/instructions/{name}.instructions.md" }, text: $"Reading the {name} instructions first.").Output!;

    /// <summary>generic+none: write the artefact with a heredoc, run the check, submit.</summary>
    private static IReadOnlyList<ModelOutput> GenericPlainPlan(Sample sample, string referenceRoot)
    {
        var artefact = HveDataset.Artefact(sample.Metadata);
        var check = HveDataset.Check(sample.Metadata);
        return [.. GenericTail(sample, referenceRoot), ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer = Summary(artefact, check) }, text: "Done.").Output!];
    }

    /// <summary>
    /// generic+hve: first <c>cat</c> every skill, prompt command and instruction file the sample names (the plugin files under
    /// <paramref name="sandboxPluginDirectory"/>, the instruction files from the workspace overlay), then write, check and submit.
    /// </summary>
    private static IReadOnlyList<ModelOutput> GenericHvePlan(Sample sample, string referenceRoot, string pluginDirectory, string sandboxPluginDirectory)
    {
        var artefact = HveDataset.Artefact(sample.Metadata);
        var check = HveDataset.Check(sample.Metadata);
        var plan = new List<ModelOutput>();
        var paths = ReadPaths(HveDataset.Components(sample.Metadata), pluginDirectory, sandboxPluginDirectory);
        if (paths.Count > 0)
        {
            var cmd = "cat " + string.Join(" ", paths.Select(path => $"'{path}'"));
            plan.Add(ScriptedTurn.ToolCall("bash", new { cmd }, text: "Reading the HVE skill and instruction files the task names.").Output!);
        }

        plan.AddRange(GenericTail(sample, referenceRoot));
        var summary = $"Done. I read the HVE skills and instruction files the task names, created `{artefact}` following them" + (check.Length > 0 ? $" and verified it with `{check}`." : ".");
        plan.Add(ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer = summary }, text: "Done.").Output!);
        return plan;
    }

    /// <summary>The generic turns before the submit: the heredoc write, then the check when the sample has one.</summary>
    private static IEnumerable<ModelOutput> GenericTail(Sample sample, string referenceRoot)
    {
        var artefact = HveDataset.Artefact(sample.Metadata);
        var check = HveDataset.Check(sample.Metadata);
        var directory = artefact.Contains('/') ? artefact[..artefact.LastIndexOf('/')] : ".";
        var command = $"mkdir -p '{directory}' && cat > '{artefact}' <<'{HereDoc}'\n{Reference(referenceRoot, sample)}\n{HereDoc}\n";
        if (artefact.EndsWith(".sh", StringComparison.Ordinal))
        {
            command += $"chmod +x '{artefact}'\n";
        }

        yield return ScriptedTurn.ToolCall("bash", new { cmd = command }, text: $"Writing {artefact}.").Output!;
        if (check.Length > 0)
        {
            yield return ScriptedTurn.ToolCall("bash", new { cmd = check }, text: "Verifying with the task's check.").Output!;
        }
    }

    /// <summary>
    /// The files the generic+hve plan reads, in component order by kind (skills, then prompt commands, then instruction
    /// files): a skill's <c>SKILL.md</c> and a prompt's <c>.prompt.md</c> located in the host plugin copy and re-rooted at
    /// the sandbox plugin directory; instruction files by their workspace-relative overlay path. A skill or prompt the
    /// plugin lacks is a dataset/plugin drift and fails loudly.
    /// </summary>
    private static IReadOnlyList<string> ReadPaths(IReadOnlyList<string> components, string pluginDirectory, string sandboxPluginDirectory)
    {
        var root = sandboxPluginDirectory.TrimEnd('/');
        var paths = new List<string>();
        foreach (var skill in Named(components, "skill"))
        {
            var file = Directory.EnumerateFiles(pluginDirectory, "SKILL.md", SearchOption.AllDirectories)
                .FirstOrDefault(candidate => Path.GetFileName(Path.GetDirectoryName(candidate)!) == skill)
                ?? throw new InvalidOperationException($"The plugin at {pluginDirectory} has no skill '{skill}'.");
            paths.Add(root + "/" + Relative(pluginDirectory, file));
        }

        foreach (var prompt in Named(components, "prompt"))
        {
            var file = Directory.EnumerateFiles(pluginDirectory, prompt + ".prompt.md", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"The plugin at {pluginDirectory} has no prompt '{prompt}'.");
            paths.Add(root + "/" + Relative(pluginDirectory, file));
        }

        foreach (var instructions in Named(components, "instructions"))
        {
            paths.Add($".github/instructions/{instructions}.instructions.md");
        }

        return paths;
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>The names of the <c>hve_components</c> entries of one kind (<c>skill/x</c> gives <c>x</c>), in metadata order.</summary>
    private static IReadOnlyList<string> Named(IReadOnlyList<string> components, string kind) =>
        components.Where(component => component.StartsWith(kind + "/", StringComparison.Ordinal)).Select(component => component[(kind.Length + 1)..]).ToList();

    private static string Summary(string artefact, string check) =>
        $"Done. I created `{artefact}` following the repository instructions" + (check.Length > 0 ? $" and verified it with `{check}`." : ".");

    /// <summary>
    /// The reference artefact's text. It travels inside a <c>bash -c</c> heredoc under the generic harness, where the
    /// fake sandbox rewrites <c>/workspace</c>, <c>/opt</c> and <c>/tmp</c> path tokens to host paths, so a reference that
    /// mentions one (none does today) is refused rather than silently altered; likewise the heredoc delimiter.
    /// </summary>
    private static string Reference(string referenceRoot, Sample sample)
    {
        var path = ReferencePath(referenceRoot, sample);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"No reference solution for sample {sample.Id} at {path}.");
        }

        var text = File.ReadAllText(path);
        if (text.Contains(HereDoc, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The reference {path} contains the heredoc delimiter {HereDoc}.");
        }

        if (ScriptedSandboxEnvironment.SandboxPathTokenIn(text) is { } token)
        {
            throw new InvalidOperationException($"The reference {path} contains a sandbox path token ({token}); the fake sandbox would rewrite it.");
        }

        return text.TrimEnd('\n');
    }

    /// <summary>A rough token count so the run reports usage the way a real model would.</summary>
    private static ModelOutput WithUsage(IReadOnlyList<ChatMessage> messages, ModelOutput output)
    {
        var inputTokens = messages.Sum(message => message.Text.Length) / 4 + 1;
        var completion = output.Completion.Length + (output.Message.ToolCalls?.Sum(call => call.Arguments.ToJsonString().Length) ?? 0);
        var outputTokens = completion / 4 + 1;
        return output with { Usage = new ModelUsage(inputTokens, outputTokens, inputTokens + outputTokens) };
    }
}
