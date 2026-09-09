using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Components;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// COMPONENT: Scorer.
///
/// A scorer is a delegate <c>(TaskState, Target, CancellationToken) -> Score</c> plus the metrics that aggregate its
/// per-sample scores. Every task runs four scorers side by side, each answering a different question about the run:
///
/// <list type="bullet">
///   <item><see cref="ExecCheck"/> (<c>hve_check</c>): did the artefact pass? Restores the sample's check assets from
///   <c>hve/checks/&lt;id&gt;/</c> (the grader scripts, answer keys and pristine tests the agent never gets to keep
///   or edit) into the sandbox, then runs <c>metadata.check</c> there (unit tests, a shell test, a findings checker);
///   exit 0 is correct.</item>
///   <item><see cref="ArtefactReported"/> (<c>artefact_reported</c>): did the agent report what it produced? The built-in
///   <see cref="Scorers.Includes"/> scorer over the final assistant message, with the artefact path as its target.</item>
///   <item><see cref="ArtefactQuality"/> (<c>artefact_quality</c>): is the artefact good beyond the check? The built-in
///   <see cref="Scorers.ModelGradedQa"/> scorer grading the artefact's text against the sample's target and rubric
///   (partial credit allowed), the way the review tasks of the HVE Core docs are judged.</item>
///   <item><see cref="ArtefactUsed"/> (<c>hve_artefact_used</c>): did the run actually use the HVE plugin pieces it was
///   designed around? A custom scorer over the transcript: the <c>copilot_cli</c> info events (the CLI's own JSONL: skill
///   invocations, sub-agent starts, instruction files viewed) and the bridged model events (whether the custom agent's
///   body reached the system prompt). Reports the fraction of <c>metadata.hve_components</c> with evidence.</item>
/// </list>
/// The first three report <c>accuracy</c> and <c>stderr</c>; the last reports <c>mean</c> and <c>stderr</c>. The suite
/// adds a <see cref="Metrics.Grouped"/> of each scorer's own headline metric per <c>kind</c> (see <see cref="All"/>),
/// so <c>hve_artefact_used</c> keeps its <c>mean</c> label in every task instead of being relabelled by a task-level
/// override.
/// </summary>
public static class HveScorers
{
    public const string ExecCheckName = "hve_check";

    public const string ArtefactReportedName = "artefact_reported";

    public const string ArtefactQualityName = "artefact_quality";

    public const string ArtefactUsedName = "hve_artefact_used";

    /// <summary>Evidence weights: a component that was invoked, viewed or embedded counts fully; one merely listed to the model counts half.</summary>
    public const double StrongEvidence = 1.0;

    public const double WeakEvidence = 0.5;

    /// <summary>Wall-clock budget of one check command.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every scorer the tasks run; the first one supplies the headline metric. <paramref name="groupBy"/> (the suite
    /// passes <c>kind</c>) adds Python's <c>grouped(&lt;headline&gt;, "kind")</c> to each scorer with that scorer's own
    /// headline metric, accuracy or mean; <paramref name="checksRoot"/> is where the check assets live (null: the
    /// demo's <c>hve/checks</c>).
    /// </summary>
    public static IReadOnlyList<ScorerDef> All(Model? grader = null, string? pluginDirectory = null, string? checksRoot = null, string? groupBy = null)
    {
        ScorerDef[] scorers = [ExecCheck(checksRoot), ArtefactReported(), ArtefactQuality(grader), ArtefactUsed(pluginDirectory)];
        return groupBy is null
            ? scorers
            : scorers.Select(scorer => scorer with { Metrics = [.. scorer.Metrics, Metrics.Grouped(scorer.Metrics[0], groupBy)] }).ToList();
    }

    /// <summary>
    /// Restores the sample's check assets (<c>&lt;checksRoot&gt;/&lt;sample id&gt;/**</c>, written to the same relative
    /// paths under the sandbox working directory, over whatever the agent left there), then runs
    /// <c>metadata.check</c> with <c>bash -c</c> in the sample's sandbox (its working directory is the workspace) and
    /// scores exit 0 as correct. The command's output tail is the explanation, so a failing test's message is in the
    /// log next to the score; the metadata lists the restored files and any the agent had modified.
    /// </summary>
    public static ScorerDef ExecCheck(string? checksRoot = null) => Scorers.Custom(
        ExecCheckName,
        async (state, _, cancellationToken) =>
        {
            var check = HveDataset.Check(state.Metadata);
            var artefact = HveDataset.Artefact(state.Metadata);
            if (check.Length == 0)
            {
                return Score.Unscored("The sample has no metadata.check command.", answer: artefact);
            }

            var sandbox = SampleContext.Require().Sandbox();
            var restored = await RestoreCheckAssetsAsync(sandbox, checksRoot ?? HveData.ChecksRoot, Convert.ToString(state.SampleId, CultureInfo.InvariantCulture) ?? "", cancellationToken).ConfigureAwait(false);
            ExecResultView result;
            try
            {
                var exec = await sandbox.ExecAsync(["bash", "-c", check], timeout: CheckTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
                result = new ExecResultView(exec.Success, exec.ReturnCode, exec.Stdout, exec.Stderr);
            }
            catch (TimeoutException)
            {
                result = new ExecResultView(false, -1, "", $"the check did not finish within {CheckTimeout.TotalMinutes} minutes");
            }

            var output = (result.Stdout + (result.Stderr.Length > 0 ? "\n" + result.Stderr : "")).Trim();
            var modified = restored.Where(file => file.Modified).Select(file => file.Path).ToList();
            var note = modified.Count > 0 ? $"\n(the agent had modified {string.Join(", ", modified)}; the pristine copies were restored before the check)" : "";
            return new Score(result.Success ? ScoreConstants.Correct : ScoreConstants.Incorrect)
            {
                Answer = artefact,
                Explanation = $"`{check}` exited {result.ReturnCode}" + (output.Length > 0 ? ":\n" + Tail(output, 1500) : ".") + note,
                Metadata = new Dictionary<string, object?>
                {
                    ["check"] = check,
                    ["exit_code"] = result.ReturnCode,
                    ["check_files"] = restored.Select(file => file.Path).ToList(),
                    ["check_files_modified"] = modified,
                },
            };
        },
        Metrics.Accuracy(),
        Metrics.Stderr());

    /// <summary>A check asset written into the sandbox; <c>Modified</c> when the sandbox already held a different version of it.</summary>
    public sealed record RestoredFile(string Path, bool Modified);

    /// <summary>
    /// Writes every file under <c>&lt;checksRoot&gt;/&lt;sampleId&gt;/</c> to its relative path in the sandbox (the
    /// working directory), reporting which ones the sandbox already held with different content. No directory for
    /// the sample means nothing to restore.
    /// </summary>
    public static async Task<IReadOnlyList<RestoredFile>> RestoreCheckAssetsAsync(ISandboxEnvironment sandbox, string checksRoot, string sampleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(checksRoot);
        ArgumentNullException.ThrowIfNull(sampleId);
        var directory = Path.Combine(checksRoot, sampleId);
        if (sampleId.Length == 0 || !Directory.Exists(directory))
        {
            return [];
        }

        var restored = new List<RestoredFile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.Contains("__pycache__", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var pristine = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            byte[]? existing;
            try
            {
                existing = await sandbox.ReadFileBytesAsync(relative, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                existing = null;
            }

            await sandbox.WriteFileAsync(relative, pristine, cancellationToken).ConfigureAwait(false);
            restored.Add(new RestoredFile(relative, existing is not null && !existing.AsSpan().SequenceEqual(pristine)));
        }

        return restored;
    }

    /// <summary>
    /// The built-in <see cref="Scorers.Includes"/> scorer (the CTF docs' scorer) applied with a per-sample target: the
    /// artefact path from <c>metadata.artefact</c> instead of the sample's descriptive target. Correct when the agent's
    /// final message mentions the file it produced, which both system messages ask for.
    /// </summary>
    public static ScorerDef ArtefactReported()
    {
        var includes = Scorers.Includes();
        return Scorers.Custom(
            ArtefactReportedName,
            (state, _, cancellationToken) =>
            {
                var artefact = HveDataset.Artefact(state.Metadata);
                return artefact.Length == 0
                    ? Task.FromResult(Score.Unscored("The sample has no metadata.artefact path."))
                    : includes.Score(state, new Target(artefact), cancellationToken);
            },
            Metrics.Accuracy(),
            Metrics.Stderr());
    }

    /// <summary>
    /// The built-in <see cref="Scorers.ModelGradedQa"/> scorer over the artefact: the file is read back from the
    /// sandbox and, together with the agent's final message, graded against the sample's target (the criterion) and
    /// its <c>metadata.rubric</c> (added to the grading instructions) with partial credit. <paramref name="grader"/>
    /// null means the sample's active model grades, as in Python.
    /// </summary>
    public static ScorerDef ArtefactQuality(Model? grader = null) => Scorers.Custom(
        ArtefactQualityName,
        async (state, target, cancellationToken) =>
        {
            var artefact = HveDataset.Artefact(state.Metadata);
            var rubric = HveDataset.Rubric(state.Metadata);
            var content = await ReadArtefactAsync(artefact, cancellationToken).ConfigureAwait(false);
            var submission = content is null
                ? $"(the artefact {artefact} does not exist)\n\nAgent's final message:\n{state.Output.Completion}"
                : $"Contents of {artefact}:\n\n{content}\n\nAgent's final message:\n{state.Output.Completion}";
            var graded = state.WithMessages(state.Messages, ModelOutput.FromContent(state.Model, submission));
            var scorer = Scorers.ModelGradedQa(instructions: GradingInstructions(rubric), partialCredit: true, model: grader);
            var score = await scorer.Score(graded, target, cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, object?>(score.Metadata ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
            {
                ["artefact"] = artefact,
                ["artefact_exists"] = content is not null,
            };
            return score with { Answer = artefact, Metadata = metadata };
        },
        Metrics.Accuracy(),
        Metrics.Stderr());

    /// <summary>The rubric prepended to the default model-graded instructions (which carry the <c>GRADE: C/P/I</c> contract).</summary>
    public static string GradingInstructions(string rubric) =>
        (rubric.Length > 0 ? $"Apply this rubric when judging the submission against the criterion:\n{rubric}\n\n" : "")
        + ModelGraded.DefaultInstructions(partialCredit: true);

    /// <summary>
    /// The custom scorer: for each entry of <c>metadata.hve_components</c> looks for evidence in the sample's
    /// transcript that the run used it (see <see cref="Evidence"/>) and scores the mean evidence weight, so a run that
    /// got the check right by ignoring the plugin scores 0 here. <paramref name="pluginDirectory"/> is where the
    /// agent bodies are read from to recognise them in the system prompt; null means the vendored plugin.
    /// </summary>
    public static ScorerDef ArtefactUsed(string? pluginDirectory = null) => Scorers.Custom(
        ArtefactUsedName,
        (state, _, _) =>
        {
            var components = HveDataset.Components(state.Metadata);
            if (components.Count == 0)
            {
                return Task.FromResult(Score.Unscored("The sample lists no hve_components."));
            }

            var events = SampleContext.Current?.Transcript.Events ?? [];
            var evidence = Evidence(components, events, pluginDirectory ?? HveData.PluginDirectory);
            var value = evidence.Values.Average(e => e.Weight);
            var lines = evidence.Select(pair => $"{pair.Key}: {pair.Value.Note}");
            return Task.FromResult(new Score(new ScoreValue.Num(value))
            {
                Answer = string.Join(", ", evidence.Where(pair => pair.Value.Weight > 0).Select(pair => pair.Key)),
                Explanation = string.Join("\n", lines),
                Metadata = evidence.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Weight, StringComparer.Ordinal),
            });
        },
        Metrics.Mean(),
        Metrics.Stderr());

    /// <summary>What the transcript shows for one component.</summary>
    public sealed record ComponentEvidence(double Weight, string Note);

    /// <summary>
    /// The evidence rules, one per component kind, over the sample's transcript events:
    /// <list type="bullet">
    ///   <item><c>skill/x</c> and <c>prompt/x</c>: strong when a <c>tool.execution_start</c> line of the CLI invoked the <c>skill</c>
    ///   tool for it; weak when <c>session.skills_loaded</c> announced it.</item>
    ///   <item><c>agent/x</c>: strong when a <c>subagent.started</c> line names <c>hve-core:x</c> or a bridged model event's system
    ///   prompt embeds the agent's markdown body (what <c>--agent</c> does); weak when the <c>task</c> tool was asked for it.</item>
    ///   <item><c>instructions/x</c>: strong when the CLI viewed or read <c>x.instructions.md</c> (a <c>view</c>, <c>bash</c> or
    ///   <c>grep</c> call naming it); weak when a model event's system prompt listed the file for the model to read.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, ComponentEvidence> Evidence(IReadOnlyList<string> components, IReadOnlyList<TranscriptEvent> events, string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(pluginDirectory);
        var cli = events.OfType<InfoEvent>()
            .Where(e => e.Source == CopilotCliEvents.Source && e.Data is JsonObject)
            .Select(e => (JsonObject)e.Data!)
            .ToList();
        var systemPrompts = events.OfType<ModelEvent>()
            .SelectMany(e => e.Input.OfType<ChatMessageSystem>().Select(m => m.Text))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, ComponentEvidence>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            var separator = component.IndexOf('/', StringComparison.Ordinal);
            var kind = separator > 0 ? component[..separator] : component;
            var name = separator > 0 ? component[(separator + 1)..] : "";
            result[component] = kind switch
            {
                "skill" => SkillEvidence(cli, name, name),
                "prompt" => SkillEvidence(cli, name + ".prompt", name),
                "agent" => AgentEvidence(cli, systemPrompts, name, pluginDirectory),
                "instructions" => InstructionsEvidence(cli, systemPrompts, name),
                _ => new ComponentEvidence(0, $"unknown component kind '{kind}'"),
            };
        }

        return result;
    }

    private static ComponentEvidence SkillEvidence(IReadOnlyList<JsonObject> cli, string skillName, string alias)
    {
        var invoked = ToolStarts(cli).Any(call => call.Name == "skill" && (Str(call.Arguments?["skill"]) is { } s && (s == skillName || s == alias || s.EndsWith(":" + skillName, StringComparison.Ordinal))));
        if (invoked)
        {
            return new ComponentEvidence(StrongEvidence, $"the skill tool loaded '{skillName}'");
        }

        var loaded = cli.Where(line => Type(line) == "session.skills_loaded")
            .SelectMany(line => (line["data"]?["skills"] as JsonArray) ?? [])
            .Any(skill => Str(skill?["name"]) is { } n && (n == skillName || n == alias));
        return loaded
            ? new ComponentEvidence(WeakEvidence, $"'{skillName}' was announced in session.skills_loaded but never invoked")
            : new ComponentEvidence(0, $"no trace of '{skillName}'");
    }

    private static ComponentEvidence AgentEvidence(IReadOnlyList<JsonObject> cli, IReadOnlyList<string> systemPrompts, string agentName, string pluginDirectory)
    {
        var qualified = "hve-core:" + agentName;
        var started = cli.Any(line => Type(line) == "subagent.started" && Str(line["data"]?["agentName"]) == qualified);
        if (started)
        {
            return new ComponentEvidence(StrongEvidence, $"sub-agent {qualified} was started");
        }

        // A whole-line match: "# Code Review" is a prefix of both sub-agent headings, so a substring would credit the
        // orchestrator whenever a sub-agent was embedded.
        var marker = AgentBodyMarker(pluginDirectory, agentName);
        if (marker is not null && systemPrompts.Any(prompt => ContainsLine(prompt, marker)))
        {
            return new ComponentEvidence(StrongEvidence, $"the {qualified} agent body was embedded in the system prompt");
        }

        var requested = ToolStarts(cli).Any(call => call.Name == "task" && Str(call.Arguments?["agent_type"]) == qualified);
        return requested
            ? new ComponentEvidence(WeakEvidence, $"the task tool asked for {qualified} but no sub-agent start was recorded")
            : new ComponentEvidence(0, $"no trace of {qualified}");
    }

    private static ComponentEvidence InstructionsEvidence(IReadOnlyList<JsonObject> cli, IReadOnlyList<string> systemPrompts, string name)
    {
        var file = name + ".instructions.md";
        var read = ToolStarts(cli).Any(call => call.Name is "view" or "bash" or "grep" or "glob" && call.Arguments?.ToJsonString().Contains(file, StringComparison.Ordinal) == true);
        if (read)
        {
            return new ComponentEvidence(StrongEvidence, $"{file} was read by a tool call");
        }

        var listed = systemPrompts.Any(prompt => prompt.Contains(file, StringComparison.Ordinal));
        return listed
            ? new ComponentEvidence(WeakEvidence, $"{file} was listed in the system prompt but never read")
            : new ComponentEvidence(0, $"no trace of {file}");
    }

    /// <summary>Whether <paramref name="text"/> has a line equal (after trimming) to <paramref name="line"/>.</summary>
    public static bool ContainsLine(string text, string line)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(line);
        return text.Split('\n').Any(candidate => candidate.Trim() == line);
    }

    /// <summary>
    /// A line of the agent's markdown body (the first non-empty line after the frontmatter, its H1 for every
    /// vendored agent) that the CLI embeds in the system prompt when the agent is selected, matched as a whole
    /// line; null when the agent file is not in the plugin.
    /// </summary>
    public static string? AgentBodyMarker(string pluginDirectory, string agentName)
    {
        var agentsDirectory = Path.Combine(pluginDirectory, ".github", "agents");
        if (!Directory.Exists(agentsDirectory))
        {
            return null;
        }

        var file = Directory.EnumerateFiles(agentsDirectory, agentName + ".agent.md", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        var lines = File.ReadAllLines(file);
        var index = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            index = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            index = index < 0 ? lines.Length : index + 1;
        }

        return lines.Skip(index).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
    }

    private static IEnumerable<(string Name, JsonObject? Arguments)> ToolStarts(IReadOnlyList<JsonObject> cli) =>
        cli.Where(line => Type(line) == "tool.execution_start")
            .Select(line => (Str(line["data"]?["toolName"]) ?? "", line["data"]?["arguments"] as JsonObject));

    private static string? Type(JsonObject line) => Str(line["type"]);

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static async Task<string?> ReadArtefactAsync(string artefact, CancellationToken cancellationToken)
    {
        if (artefact.Length == 0 || SampleContext.Current is not { Sandboxes: not null } context)
        {
            return null;
        }

        try
        {
            return await context.Sandbox().ReadFileAsync(artefact, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Tail(string text, int length) =>
        text.Length <= length ? text : "…" + text[^length..];

    /// <summary>A sandbox exec result in the shape the check scorer reasons about.</summary>
    private sealed record ExecResultView(bool Success, int ReturnCode, string Stdout, string Stderr);
}
