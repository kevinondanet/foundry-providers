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
/// so far, so the run is deterministic and still exercises the real pipeline: through the Copilot CLI (real or fake)
/// it loads the skill or reads the instruction file the sample names, creates the artefact from the reference
/// solution under <c>hve/reference/&lt;id&gt;/</c>, runs the sample's check command, and reports the file it wrote; through
/// the baseline <c>basic_agent</c> it does the same with <c>bash</c> heredocs and <c>submit</c>. As the grader of
/// <c>artefact_quality</c> it answers <c>GRADE: C</c>. The sample is recognised from its input text inside the first user message.
/// </summary>
public static class FakeHveModel
{
    public const string ModelName = "hve-scripted";

    private const int TurnBudget = 256;

    private const string HereDoc = "HVE_EOF";

    /// <summary>The scripted model over <paramref name="dataset"/>'s samples and the reference solutions under <paramref name="referenceRoot"/>.</summary>
    public static Model Create(IDataset dataset, string referenceRoot)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(referenceRoot);
        var samples = dataset.ToList();
        return new Model(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From((messages, tools) => Respond(samples, referenceRoot, messages, tools)), TurnBudget), ModelName));
    }

    /// <summary>The scripted model over the demo's own dataset and references.</summary>
    public static Model Create() => Create(HveDataset.Load(), HveData.ReferenceRoot);

    /// <summary>The reference artefact of a sample: <c>&lt;referenceRoot&gt;/&lt;id&gt;/&lt;artefact&gt;</c>.</summary>
    public static string ReferencePath(string referenceRoot, Sample sample) =>
        Path.Combine(referenceRoot, Convert.ToString(sample.Id, CultureInfo.InvariantCulture) ?? "", HveDataset.Artefact(sample.Metadata));

    private static ModelOutput Respond(IReadOnlyList<Sample> samples, string referenceRoot, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
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

        var toolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var step = messages.Count(message => message is ChatMessageAssistant);
        output = toolNames.Contains(Solvers.BasicAgentSubmitName)
            ? BasicTurn(sample, referenceRoot, step)
            : CopilotTurn(sample, referenceRoot, step, toolNames);
        return WithUsage(messages, output);
    }

    /// <summary>Through the CLI: load the named skill (or read the instruction file), create the artefact, run the check, summarise.</summary>
    private static ModelOutput CopilotTurn(Sample sample, string referenceRoot, int step, IReadOnlySet<string> toolNames)
    {
        var artefact = HveDataset.Artefact(sample.Metadata);
        var check = HveDataset.Check(sample.Metadata);
        var components = HveDataset.Components(sample.Metadata);
        var skill = components.FirstOrDefault(c => c.StartsWith("skill/", StringComparison.Ordinal))?["skill/".Length..];
        var prompt = components.FirstOrDefault(c => c.StartsWith("prompt/", StringComparison.Ordinal))?["prompt/".Length..];
        var instructions = components.FirstOrDefault(c => c.StartsWith("instructions/", StringComparison.Ordinal))?["instructions/".Length..];
        var sandboxPath = HveData.SandboxWorkingDirectory + "/" + artefact;

        switch (step)
        {
            case 0 when skill is not null && toolNames.Contains("skill"):
                return ScriptedTurn.ToolCall("skill", new { skill }, text: $"Loading the {skill} skill as my checklist before I write anything.").Output!;
            case 0 when prompt is not null && toolNames.Contains("skill"):
                return ScriptedTurn.ToolCall("skill", new { skill = prompt + ".prompt" }, text: $"Loading the {prompt} prompt command.").Output!;
            case 0 when instructions is not null && toolNames.Contains("view"):
                return ScriptedTurn.ToolCall("view", new { path = $"{HveData.SandboxWorkingDirectory}/.github/instructions/{instructions}.instructions.md" }, text: $"Reading the {instructions} instructions first.").Output!;
            case 0:
            case 1:
                return ScriptedTurn.ToolCall("create", new { path = sandboxPath, file_text = Reference(referenceRoot, sample) }, text: $"Writing {artefact}.").Output!;
            case 2 when check.Length > 0 && toolNames.Contains("bash"):
            {
                // A create'd shell script is not executable; the real model would chmod it too.
                var command = artefact.EndsWith(".sh", StringComparison.Ordinal) ? $"chmod +x '{artefact}' && {check}" : check;
                return ScriptedTurn.ToolCall("bash", new { command, description = "verify the artefact" }, text: "Verifying with the task's check.").Output!;
            }

            default:
                return ScriptedTurn.Text(Summary(artefact, check)).Output!;
        }
    }

    /// <summary>Through the baseline: write the artefact with a heredoc, run the check, submit.</summary>
    private static ModelOutput BasicTurn(Sample sample, string referenceRoot, int step)
    {
        var artefact = HveDataset.Artefact(sample.Metadata);
        var check = HveDataset.Check(sample.Metadata);
        switch (step)
        {
            case 0:
            {
                var directory = artefact.Contains('/') ? artefact[..artefact.LastIndexOf('/')] : ".";
                var command = $"mkdir -p '{directory}' && cat > '{artefact}' <<'{HereDoc}'\n{Reference(referenceRoot, sample)}\n{HereDoc}\n";
                if (artefact.EndsWith(".sh", StringComparison.Ordinal))
                {
                    command += $"chmod +x '{artefact}'\n";
                }

                return ScriptedTurn.ToolCall("bash", new { cmd = command }, text: $"Writing {artefact}.").Output!;
            }

            case 1 when check.Length > 0:
                return ScriptedTurn.ToolCall("bash", new { cmd = check }, text: "Verifying with the task's check.").Output!;
            default:
                return ScriptedTurn.ToolCall(Solvers.BasicAgentSubmitName, new { answer = Summary(artefact, check) }, text: "Done.").Output!;
        }
    }

    private static string Summary(string artefact, string check) =>
        $"Done. I created `{artefact}` following the repository instructions" + (check.Length > 0 ? $" and verified it with `{check}`." : ".");

    private static string Reference(string referenceRoot, Sample sample)
    {
        var path = ReferencePath(referenceRoot, sample);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"No reference solution for sample {sample.Id} at {path}.");
        }

        var text = File.ReadAllText(path);
        return text.Contains(HereDoc, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"The reference {path} contains the heredoc delimiter {HereDoc}.")
            : text.TrimEnd('\n');
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
