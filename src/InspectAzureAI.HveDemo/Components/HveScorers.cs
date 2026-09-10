using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Components;

using Model = InspectAzureAI.Eval.Model.Model;
using ToolCall = (string Name, System.Text.Json.Nodes.JsonObject? Arguments, string? Result, bool Failed);

/// <summary>
/// COMPONENT: Scorer.
///
/// A scorer is a delegate <c>(TaskState, Target, CancellationToken) -> Score</c> plus the metrics that aggregate its
/// per-sample scores. Every task runs up to four scorers side by side, each answering a different question about the run:
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
///   body reached the system prompt), or, under the generic harness, Inspect's own <see cref="ToolEvent"/>s of the
///   <c>bash</c> tool and the briefing in the model events. Reports the fraction of <c>metadata.hve_components</c> with
///   evidence. Left out of the task under <c>--framework none</c> (there is nothing to use).</item>
/// </list>
/// The first three report <c>accuracy</c> and <c>stderr</c>; the last reports <c>mean</c> and <c>stderr</c>. The suite
/// adds a <see cref="Metrics.Grouped"/> of each scorer's own headline metric per <c>kind</c> (see <see cref="All"/>),
/// so <c>hve_artefact_used</c> keeps its <c>mean</c> label in every task instead of being relabelled by a task-level
/// override.
/// </summary>
public static partial class HveScorers
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
    /// final message mentions the file it produced, which every briefing (all four cells) asks for.
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
    /// The evidence rules, one per component kind, over the sample's transcript events. The two harnesses leave two
    /// different trails, so every rule reads both: the Copilot CLI's own JSONL (the <c>copilot_cli</c> info events:
    /// <c>tool.execution_start</c>, <c>session.skills_loaded</c>, <c>subagent.started</c>) and Inspect's own
    /// <see cref="ToolEvent"/>s (the generic harness's <c>bash</c> tool; under the copilot harness the bridge records the
    /// CLI's calls this way too, which is harmless because every rule asks "any"). Per component the first rule that
    /// matches wins; strong is <see cref="StrongEvidence"/>, weak <see cref="WeakEvidence"/>, otherwise 0:
    /// <list type="bullet">
    ///   <item><c>skill/x</c>: strong when the <c>skill</c> tool was invoked for it, a read tool (<c>view</c>, <c>bash</c>,
    ///   <c>grep</c>, <c>glob</c>) named <c>/x/SKILL.md</c>, <c>/x/references/</c> or <c>/x/templates/</c>, or a read tool's
    ///   result carries the skill's front matter line <c>name: x</c> (a <c>cd</c> then a relative <c>cat</c>, a globbed
    ///   <c>*/*/SKILL.md</c> or a <c>find -exec cat</c> all read the file without naming its path); weak when
    ///   <c>session.skills_loaded</c> announced it or a system prompt named it as a whole token (so <c>rpi-plan</c> is not
    ///   credited for <c>rpi-plan-critique</c>).</item>
    ///   <item><c>prompt/x</c>: strong when the <c>skill</c> tool was invoked for <c>x.prompt</c> or a read tool named
    ///   <c>/x.prompt.md</c>; weak when <c>x.prompt</c> was announced or listed in a system prompt.</item>
    ///   <item><c>agent/x</c>: strong when a <c>subagent.started</c> line names <c>hve-core:x</c>, a system prompt embeds the
    ///   agent's markdown body (what the CLI's <c>--agent</c> and the generic HVE briefing both do), a read tool named
    ///   <c>/x.agent.md</c>, or a read tool's result carries the body's first line; weak when the <c>task</c> tool was asked
    ///   for it, or the briefing told the agent to read its definition (<see cref="HveBriefing.AgentFallbackPrefix"/>).</item>
    ///   <item><c>instructions/x</c>: strong when a read tool named <c>x.instructions.md</c>; weak when a system prompt listed
    ///   the file for the model to read.</item>
    /// </list>
    /// What counts as "named" a path is deliberately narrow, because under the generic harness every write is a <c>bash</c>
    /// call too: the path rules match over the call's arguments with heredoc bodies cut out and the CLI's <c>description</c>
    /// field ignored (<see cref="ReadText"/>), so a research note that cites <c>python-script.instructions.md</c> inside a
    /// <c>cat &gt; ... &lt;&lt;'EOF'</c> is not a read, and never over the arguments' JSON text (<see cref="ArgumentText"/>).
    /// A read that failed does not count either: a call the engine or the CLI marked as failed, or one whose result reports
    /// <c>No such file or directory</c> for the very path (a wrong <c>--plugin-dir</c>, a mistyped skill directory), so a
    /// <c>cat a b</c> with <c>b</c> missing still credits <c>a</c>. The briefings list every component they provision, so a
    /// run that reads nothing still scores the weak floor (the README quotes it); a compliant run scores 1.0.
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
        // The CLI's start lines carry no outcome (never failed, no result); Inspect's ToolEvents carry both.
        var toolCalls = ToolStarts(cli)
            .Concat(events.OfType<ToolEvent>().Select(e => new ToolCall(e.Function, e.Arguments, e.Result, e.Error is not null || e.Failed == true)))
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
                "skill" => SkillEvidence(cli, toolCalls, systemPrompts, name),
                "prompt" => PromptEvidence(cli, toolCalls, systemPrompts, name),
                "agent" => AgentEvidence(cli, toolCalls, systemPrompts, name, pluginDirectory),
                "instructions" => InstructionsEvidence(toolCalls, systemPrompts, name),
                _ => new ComponentEvidence(0, $"unknown component kind '{kind}'"),
            };
        }

        return result;
    }

    /// <summary>
    /// The text the path rules match: every string value of a tool's arguments object (recursively), joined with newlines,
    /// so a path split across values never joins into a false match and no JSON escaping (<c>'</c> as <c>\u0027</c>) gets
    /// in the way. Null (a call without arguments) is the empty string.
    /// </summary>
    public static string ArgumentText(JsonObject? arguments)
    {
        if (arguments is null)
        {
            return "";
        }

        var values = new List<string>();
        Collect(arguments, values);
        return string.Join("\n", values);
    }

    /// <summary>
    /// The text the path rules actually match, narrower than <see cref="ArgumentText"/> in two ways that keep a write from
    /// looking like a read: the top-level <c>description</c> property (the CLI's bash tool carries prose about the command
    /// there) is left out, and every heredoc body (from <c>&lt;&lt;TAG</c> to the terminator line) is cut out of the command,
    /// so a file the agent writes may cite instruction files and skill paths without being credited for reading them.
    /// </summary>
    private static string ReadText(JsonObject? arguments)
    {
        if (arguments is null)
        {
            return "";
        }

        var values = new List<string>();
        foreach (var property in arguments)
        {
            if (property.Key == "description")
            {
                continue;
            }

            Collect(property.Value, values);
        }

        return HereDocBody().Replace(string.Join("\n", values), " ");
    }

    private static void Collect(JsonNode? node, List<string> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    Collect(property.Value, values);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, values);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                values.Add(text);
                break;
        }
    }

    /// <summary>
    /// The tools whose arguments name the files they read (the CLI's <c>view</c>/<c>grep</c>/<c>glob</c> and both harnesses'
    /// <c>bash</c>); a path in one of their calls is evidence the file was read. <c>submit</c> is deliberately not one: a
    /// summary that names a skill is not a read.
    /// </summary>
    private static readonly string[] ReadTools = ["view", "bash", "grep", "glob"];

    private static ComponentEvidence SkillEvidence(IReadOnlyList<JsonObject> cli, IReadOnlyList<ToolCall> toolCalls, IReadOnlyList<string> systemPrompts, string name)
    {
        if (SkillInvoked(toolCalls, name, name))
        {
            return new ComponentEvidence(StrongEvidence, $"the skill tool loaded '{name}'");
        }

        // The leading slash makes the name a whole path segment: /rpi-plan/SKILL.md never matches under rpi-plan-critique/.
        if (ReadPathMatches(toolCalls, "/" + Regex.Escape(name) + @"/(SKILL\.md|references/|templates/)"))
        {
            return new ComponentEvidence(StrongEvidence, $"the {name} skill files were read by a tool call");
        }

        // Every vendored SKILL.md opens with "name: <skill>" front matter, so a read that never spelled the path (cd + cat,
        // a glob over every skill, find -exec) is still recognised from what came back; whole-line, so rpi-plan-critique's
        // front matter does not credit rpi-plan.
        if (ReadResultMatches(toolCalls, @"(?m)^name:[ \t]*" + Regex.Escape(name) + @"[ \t]*\r?$"))
        {
            return new ComponentEvidence(StrongEvidence, $"the {name} skill's SKILL.md came back in a tool result");
        }

        if (SkillAnnounced(cli, name, name))
        {
            return new ComponentEvidence(WeakEvidence, $"'{name}' was announced in session.skills_loaded but never invoked");
        }

        // A whole-token match, so a briefing that lists rpi-plan-critique does not also credit rpi-plan.
        var token = @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])";
        return systemPrompts.Any(prompt => Regex.IsMatch(prompt, token))
            ? new ComponentEvidence(WeakEvidence, $"'{name}' was listed in the briefing but never loaded or read")
            : new ComponentEvidence(0, $"no trace of '{name}'");
    }

    private static ComponentEvidence PromptEvidence(IReadOnlyList<JsonObject> cli, IReadOnlyList<ToolCall> toolCalls, IReadOnlyList<string> systemPrompts, string name)
    {
        // The CLI registers a prompt command as the skill "<name>.prompt"; the generic harness reads <name>.prompt.md.
        var skillName = name + ".prompt";
        if (SkillInvoked(toolCalls, skillName, name))
        {
            return new ComponentEvidence(StrongEvidence, $"the skill tool loaded '{skillName}'");
        }

        if (ReadPathMatches(toolCalls, "/" + Regex.Escape(name) + @"\.prompt\.md"))
        {
            return new ComponentEvidence(StrongEvidence, $"{name}.prompt.md was read by a tool call");
        }

        if (SkillAnnounced(cli, skillName, name))
        {
            return new ComponentEvidence(WeakEvidence, $"'{skillName}' was announced in session.skills_loaded but never invoked");
        }

        return systemPrompts.Any(prompt => prompt.Contains(skillName, StringComparison.Ordinal))
            ? new ComponentEvidence(WeakEvidence, $"'{skillName}' was listed in the briefing but never loaded or read")
            : new ComponentEvidence(0, $"no trace of '{skillName}'");
    }

    private static ComponentEvidence AgentEvidence(IReadOnlyList<JsonObject> cli, IReadOnlyList<ToolCall> toolCalls, IReadOnlyList<string> systemPrompts, string agentName, string pluginDirectory)
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

        if (ReadPathMatches(toolCalls, "/" + Regex.Escape(agentName) + @"\.agent\.md"))
        {
            return new ComponentEvidence(StrongEvidence, $"{agentName}.agent.md was read by a tool call");
        }

        // The same whole-line marker over what a read tool returned: a find -exec cat or a glob over the agents directory.
        if (marker is not null && toolCalls.Any(call => IsReadTool(call) && call.Result is { } result && ContainsLine(result, marker)))
        {
            return new ComponentEvidence(StrongEvidence, $"the {qualified} agent body came back in a tool result");
        }

        var requested = toolCalls.Any(call => call.Name == "task" && Str(call.Arguments?["agent_type"]) == qualified);
        if (requested)
        {
            return new ComponentEvidence(WeakEvidence, $"the task tool asked for {qualified} but no sub-agent start was recorded");
        }

        // The generic HVE briefing's fallback when the host could not embed the body: it asks the agent to read it.
        var asked = systemPrompts.Any(prompt => prompt.Contains(HveBriefing.AgentFallbackPrefix + qualified, StringComparison.Ordinal));
        return asked
            ? new ComponentEvidence(WeakEvidence, $"the briefing asked for {qualified} but its body was neither embedded nor read")
            : new ComponentEvidence(0, $"no trace of {qualified}");
    }

    private static ComponentEvidence InstructionsEvidence(IReadOnlyList<ToolCall> toolCalls, IReadOnlyList<string> systemPrompts, string name)
    {
        var file = name + ".instructions.md";
        if (ReadPathMatches(toolCalls, Regex.Escape(file)))
        {
            return new ComponentEvidence(StrongEvidence, $"{file} was read by a tool call");
        }

        var listed = systemPrompts.Any(prompt => prompt.Contains(file, StringComparison.Ordinal));
        return listed
            ? new ComponentEvidence(WeakEvidence, $"{file} was listed in the system prompt but never read")
            : new ComponentEvidence(0, $"no trace of {file}");
    }

    /// <summary>Whether the <c>skill</c> tool was called for <paramref name="skillName"/> (or its <paramref name="alias"/>, or a <c>plugin:name</c> spelling of it), by either harness.</summary>
    private static bool SkillInvoked(IReadOnlyList<ToolCall> toolCalls, string skillName, string alias) =>
        toolCalls.Any(call => call.Name == "skill" && Str(call.Arguments?["skill"]) is { } s && (s == skillName || s == alias || s.EndsWith(":" + skillName, StringComparison.Ordinal)));

    /// <summary>Whether the CLI's <c>session.skills_loaded</c> line named the skill (the CLI lists every plugin skill at start-up).</summary>
    private static bool SkillAnnounced(IReadOnlyList<JsonObject> cli, string skillName, string alias) =>
        cli.Where(line => Type(line) == "session.skills_loaded")
            .SelectMany(line => (line["data"]?["skills"] as JsonArray) ?? [])
            .Any(skill => Str(skill?["name"]) is { } n && (n == skillName || n == alias));

    /// <summary>
    /// Whether a read tool named a path matching <paramref name="pattern"/> (over <see cref="ReadText"/>) and the read
    /// did not fail: the call itself succeeded and its result does not report that very path as missing.
    /// </summary>
    private static bool ReadPathMatches(IReadOnlyList<ToolCall> toolCalls, string pattern) =>
        toolCalls.Any(call => IsReadTool(call) && !call.Failed && Regex.IsMatch(ReadText(call.Arguments), pattern) && !ReportsMissing(call.Result, pattern));

    /// <summary>Whether a read tool's result (the text the model saw) matches <paramref name="pattern"/>; the CLI's start lines have no result.</summary>
    private static bool ReadResultMatches(IReadOnlyList<ToolCall> toolCalls, string pattern) =>
        toolCalls.Any(call => IsReadTool(call) && call.Result is { } result && Regex.IsMatch(result, pattern));

    /// <summary>
    /// Whether <paramref name="result"/> has a <c>No such file or directory</c> line naming a path that matches
    /// <paramref name="pattern"/> (the shell's <c>cat: /opt/x/SKILL.md: No such file or directory</c>), so that in
    /// <c>cat a b</c> with <c>b</c> missing only <c>b</c> loses its credit.
    /// </summary>
    private static bool ReportsMissing(string? result, string pattern) =>
        result is not null && result.Split('\n').Any(line => line.Contains("No such file or directory", StringComparison.Ordinal) && Regex.IsMatch(line, pattern));

    private static bool IsReadTool(ToolCall call) => ReadTools.Contains(call.Name, StringComparer.Ordinal);

    // A heredoc from its <<TAG (quoted or not, <<- allowed, never a <<< here-string) through the line holding the bare
    // terminator; an unterminated one runs to the end of the command. Replaced by a space so the surrounding tokens stay apart.
    [GeneratedRegex(@"(?<!<)<<-?[ \t]*(['""]?)(\w+)\1[^\n]*\n(?:(?:[\s\S]*?\n)?[ \t]*\2[ \t]*(?=\n|$)|[\s\S]*$)")]
    private static partial Regex HereDocBody();

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

    /// <summary>The CLI's own record of its tool calls: one <c>tool.execution_start</c> line per call, with the tool name and its arguments.</summary>
    private static IEnumerable<ToolCall> ToolStarts(IReadOnlyList<JsonObject> cli) =>
        cli.Where(line => Type(line) == "tool.execution_start")
            .Select(line => new ToolCall(Str(line["data"]?["toolName"]) ?? "", line["data"]?["arguments"] as JsonObject, null, false));

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
