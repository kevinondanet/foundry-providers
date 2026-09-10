using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Fake;

/// <summary>
/// The stand-in for the <c>copilot</c> binary under <c>--fake</c>, modelled on <c>examples/evals_in_eval/FakeClaudeCli.cs</c>:
/// answers the sandbox exec of the Copilot CLI launch the way the real binary would from the outside. It reads the
/// prompt, the custom agent and the plugin directory from the argv and the bridge address and token from the
/// <c>COPILOT_PROVIDER_*</c> variables the agent hands the exec, then talks to the sandbox agent bridge over HTTP
/// exactly as Copilot CLI 1.0.83 does under BYOK (<c>POST &lt;base&gt;/chat/completions</c>, <c>Authorization: Bearer</c>,
/// <c>stream: true</c> with <c>stream_options.include_usage</c>, the probe's request keys), consumes the SSE chunk stream,
/// executes the model's <c>bash</c>/<c>view</c>/<c>create</c>/<c>edit</c>/<c>skill</c> tool calls against the sample's
/// mirror directory, and prints the namespaced JSONL the agent parses (<c>session.skills_loaded</c>,
/// <c>user.message</c>, <c>assistant.message</c> with <c>toolRequests</c>, <c>tool.execution_start/complete</c>, a final
/// <c>result</c>). What the real CLI does with a plugin is reproduced where it matters to the scorers: the plugin's
/// skills are announced and listed to the model, a <c>--agent</c> body is embedded in the system prompt as the live
/// CLI's <c>&lt;agent_instructions&gt;</c> block and the tools advertised are cut down to what the agent's front matter
/// maps to (the live run showed the review sub-agents with <c>view</c>, <c>create</c>, <c>skill</c> only), the workspace
/// instruction files are tabled in the system prompt, and the <c>skill</c> tool injects a <c>skill-context</c> user
/// message. Launched without <c>--plugin-dir</c>/<c>--agent</c> (the <c>--framework none</c> cells) it announces no skills,
/// embeds no agent and offers every tool, as the real CLI does. Every bridge request is appended to <see cref="RequestLog"/>
/// in the sample's fake sandbox so tests can read it back.
/// </summary>
public sealed partial class FakeCopilotCli(Func<ScriptedSandboxEnvironment?> environment)
{
    /// <summary>Where the fake pretends the CLI is installed (answered to <c>which copilot</c>).</summary>
    public const string BinaryPath = "/usr/local/bin/copilot";

    /// <summary>The JSON-lines request log the stand-in writes into the fake sandbox: one <c>{method, path, model, status, turn}</c> per bridge request.</summary>
    public const string RequestLog = "/workspace/.fake-copilot-requests.jsonl";

    /// <summary>The most model turns one launch plays before giving up (the real CLI has no such cap; the scripted model finishes in four).</summary>
    public const int MaxTurns = 24;

    /// <summary>The tools the fake advertises (a subset of the real CLI's 17, with the real argument names from the wire probe).</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["bash", "view", "create", "edit", "skill"];

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<ScriptedSandboxEnvironment?> _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Whether <paramref name="call"/> is the agent's launch of the CLI (<c>bash -c 'exec 0&lt;/dev/null; "$@"' bash &lt;copilot&gt; -p ...</c>).</summary>
    public static bool IsLaunch(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Cmd.Count > 4 && call.Cmd[0] == "bash" && call.Cmd[2] == CopilotCliCommand.LaunchScript && call.Cmd.Skip(4).Contains("--output-format");
    }

    /// <summary>The value following <paramref name="flag"/> in the argv, or null.</summary>
    public static string? Flag(IReadOnlyList<string> cmd, string flag)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        for (var i = 0; i + 1 < cmd.Count; i++)
        {
            if (cmd[i] == flag)
            {
                return cmd[i + 1];
            }
        }

        return null;
    }

    /// <summary>Every value of a repeated flag (<c>--plugin-dir</c>).</summary>
    public static IReadOnlyList<string> Flags(IReadOnlyList<string> cmd, string flag)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var values = new List<string>();
        for (var i = 0; i + 1 < cmd.Count; i++)
        {
            if (cmd[i] == flag)
            {
                values.Add(cmd[i + 1]);
            }
        }

        return values;
    }

    /// <summary>The value of a <c>--flag=value</c> argument, or null.</summary>
    public static string? EqualsFlag(IReadOnlyList<string> cmd, string flag)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var prefix = flag + "=";
        return cmd.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    /// <summary>Plays the CLI for one launch and returns what the exec would: the JSONL on stdout, exit 0.</summary>
    public ExecResult Run(ScriptedSandboxEnvironment sandbox, FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(call);
        var env = call.Env ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!env.TryGetValue("COPILOT_PROVIDER_BASE_URL", out var baseUrl) || !env.TryGetValue("COPILOT_PROVIDER_API_KEY", out var token))
        {
            return FakeSandboxScript.Fail(1, "fake copilot: COPILOT_PROVIDER_BASE_URL and COPILOT_PROVIDER_API_KEY must be set\n");
        }

        if (env.GetValueOrDefault("COPILOT_PROVIDER_TYPE", "openai") != "openai")
        {
            return FakeSandboxScript.Fail(1, "fake copilot: only COPILOT_PROVIDER_TYPE=openai (chat completions) is supported by the fake\n");
        }

        var prompt = Flag(call.Cmd, "-p") ?? Flag(call.Cmd, "--prompt");
        if (prompt is null)
        {
            return FakeSandboxScript.Fail(1, "fake copilot: -p <prompt> is required\n");
        }

        var model = Flag(call.Cmd, "--model") ?? env.GetValueOrDefault("COPILOT_MODEL") ?? CopilotCliOptions.DefaultModel;
        var sessionId = Flag(call.Cmd, "--session-id") ?? EqualsFlag(call.Cmd, "--resume") ?? Guid.NewGuid().ToString();
        var cwd = call.Cwd ?? ScriptedSandboxEnvironment.WorkingDirectory;
        var session = new Session(this, sandbox, cwd, model, sessionId, Flags(call.Cmd, "--plugin-dir"), Flag(call.Cmd, "--agent"));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            session.Play(client, prompt);
            return FakeSandboxScript.Ok(session.Output(0, stopwatch.ElapsedMilliseconds));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or IOException)
        {
            // The real CLI reports model failures on stdout (session.error, then result with exitCode 1) and exits 1.
            session.Error(ex.Message);
            return FakeSandboxScript.Fail(1, "", session.Output(1, stopwatch.ElapsedMilliseconds));
        }
    }

    [GeneratedRegex(@"^\s*name:\s*['""]?([^'""\r\n]+)['""]?\s*$", RegexOptions.Multiline)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\s*description:\s*['""]?([^'""\r\n]+)['""]?\s*$", RegexOptions.Multiline)]
    private static partial Regex DescriptionPattern();

    [GeneratedRegex(@"^\s*applyTo:\s*['""]?([^'""\r\n]+)['""]?\s*$", RegexOptions.Multiline)]
    private static partial Regex ApplyToPattern();

    /// <summary>The entries of a front matter <c>tools:</c> key, block style (<c>  - x</c> lines) or flow style (<c>[a, b]</c>); null when there is no such key.</summary>
    internal static IReadOnlyList<string>? ToolIds(string frontmatter)
    {
        var lines = frontmatter.Split('\n');
        var index = Array.FindIndex(lines, line => line.StartsWith("tools:", StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var inline = lines[index]["tools:".Length..].Trim();
        if (inline.StartsWith('['))
        {
            return inline.Trim('[', ']').Split(',').Select(item => item.Trim().Trim('\'', '"')).Where(item => item.Length > 0).ToList();
        }

        var ids = new List<string>();
        foreach (var line in lines.Skip(index + 1))
        {
            if (!line.StartsWith(' ') || line.TrimStart().Length == 0)
            {
                break;
            }

            var item = line.Trim();
            if (item.StartsWith('-'))
            {
                ids.Add(item[1..].Trim().Trim('\'', '"'));
            }
        }

        return ids;
    }

    /// <summary>The YAML frontmatter of a markdown file (between the two <c>---</c> lines) and the body after it.</summary>
    internal static (string Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return ("", text);
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0)
        {
            return ("", text);
        }

        return (string.Join('\n', lines[1..end]), string.Join('\n', lines[(end + 1)..]).TrimStart('\n', '\r'));
    }

    private static string? Match(Regex pattern, string text) => pattern.Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>A skill (or prompt command) of the plugin as the CLI announces it.</summary>
    private sealed record PluginSkill(string Name, string Description, string Path, string BaseDirectory);

    /// <summary>One launch: the conversation, the tool executions and the JSONL lines they produced.</summary>
    private sealed class Session(FakeCopilotCli cli, ScriptedSandboxEnvironment sandbox, string cwd, string model, string sessionId, IReadOnlyList<string> pluginDirs, string? agent)
    {
        private readonly List<string> _lines = [];
        private readonly JsonArray _messages = [];
        private readonly List<PluginSkill> _skills = [];
        private readonly List<string> _filesModified = [];
        private int _shellId;
        private int _turns;
        private long _apiDurationMs;

        /// <summary>Runs the model loop: announce, prompt, then turns until the model stops calling tools.</summary>
        public void Play(HttpClient client, string prompt)
        {
            LoadPlugins();
            Emit("session.skills_loaded", new JsonObject
            {
                ["skills"] = new JsonArray(_skills.Select(skill => (JsonNode)new JsonObject
                {
                    ["name"] = skill.Name,
                    ["commandName"] = "hve-core:" + skill.Name,
                    ["description"] = skill.Description,
                    ["source"] = "plugin",
                    ["userInvocable"] = true,
                    ["enabled"] = true,
                    ["path"] = skill.Path,
                }).ToArray()),
            }, ephemeral: true);
            Emit("session.tools_updated", new JsonObject { ["model"] = model }, ephemeral: true);
            Emit("user.message", new JsonObject { ["content"] = prompt, ["turnId"] = "0" });

            _messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() });
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"<current_datetime>{DateTimeOffset.UtcNow:O}</current_datetime>\n\n{prompt}" });

            while (_turns < MaxTurns)
            {
                var turnId = _turns.ToString(CultureInfo.InvariantCulture);
                Emit("assistant.turn_start", new JsonObject { ["turnId"] = turnId });
                var completion = Complete(client);
                _turns++;

                var toolRequests = new JsonArray();
                foreach (var toolCall in completion.ToolCalls)
                {
                    toolRequests.Add(new JsonObject
                    {
                        ["toolCallId"] = toolCall.Id,
                        ["name"] = toolCall.Name,
                        ["arguments"] = ParseArguments(toolCall.Arguments),
                        ["type"] = "function",
                    });
                }

                Emit("assistant.message", new JsonObject
                {
                    ["messageId"] = Guid.NewGuid().ToString(),
                    ["model"] = model,
                    ["content"] = completion.Content,
                    ["toolRequests"] = toolRequests,
                    ["turnId"] = turnId,
                });

                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = completion.Content.Length > 0 ? completion.Content : null };
                if (completion.ToolCalls.Count > 0)
                {
                    assistant["tool_calls"] = new JsonArray(completion.ToolCalls.Select(toolCall => (JsonNode)new JsonObject
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = toolCall.Name, ["arguments"] = toolCall.Arguments },
                    }).ToArray());
                }

                _messages.Add(assistant);
                if (completion.ToolCalls.Count == 0)
                {
                    Emit("assistant.turn_end", new JsonObject { ["turnId"] = turnId });
                    Emit("assistant.idle", new JsonObject(), ephemeral: true);
                    return;
                }

                foreach (var toolCall in completion.ToolCalls)
                {
                    var arguments = ParseArguments(toolCall.Arguments);
                    Emit("tool.execution_start", new JsonObject { ["toolCallId"] = toolCall.Id, ["toolName"] = toolCall.Name, ["arguments"] = arguments.DeepClone(), ["turnId"] = turnId, ["model"] = model });
                    var (success, content, inject) = Execute(toolCall.Name, arguments);
                    Emit("tool.execution_complete", new JsonObject
                    {
                        ["toolCallId"] = toolCall.Id,
                        ["model"] = model,
                        ["turnId"] = turnId,
                        ["success"] = success,
                        ["result"] = new JsonObject { ["content"] = content, ["detailedContent"] = content },
                    });
                    _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = toolCall.Id, ["content"] = content });
                    if (inject is not null)
                    {
                        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = inject });
                    }
                }

                Emit("assistant.turn_end", new JsonObject { ["turnId"] = turnId });
            }

            throw new InvalidOperationException($"the model kept calling tools for {MaxTurns} turns");
        }

        public void Error(string message) =>
            Emit("session.error", new JsonObject { ["errorType"] = "query", ["message"] = message });

        /// <summary>The stdout: every line, then the final <c>result</c> line (no <c>data</c> wrapper, as the probe showed).</summary>
        public string Output(int exitCode, long sessionDurationMs)
        {
            var result = new JsonObject
            {
                ["type"] = "result",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["sessionId"] = sessionId,
                ["exitCode"] = exitCode,
                ["usage"] = new JsonObject
                {
                    ["premiumRequests"] = 0,
                    ["totalApiDurationMs"] = _apiDurationMs,
                    ["sessionDurationMs"] = sessionDurationMs,
                    ["codeChanges"] = new JsonObject
                    {
                        ["linesAdded"] = 0,
                        ["linesRemoved"] = 0,
                        ["filesModified"] = new JsonArray(_filesModified.Distinct(StringComparer.Ordinal).Select(file => (JsonNode)file).ToArray()),
                    },
                },
            };
            return string.Join("\n", _lines.Append(result.ToJsonString())) + "\n";
        }

        private void Emit(string type, JsonObject data, bool ephemeral = false)
        {
            var line = new JsonObject
            {
                ["type"] = type,
                ["data"] = data,
            };
            if (ephemeral)
            {
                line["ephemeral"] = true;
            }

            line["id"] = Guid.NewGuid().ToString();
            line["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
            line["parentId"] = sessionId;
            _lines.Add(line.ToJsonString());
        }

        /// <summary>One streamed <c>POST chat/completions</c> with the probe's request shape, folded back into a completion.</summary>
        private Completion Complete(HttpClient client)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(_messages.ToJsonString()),
                ["temperature"] = 0,
                ["top_p"] = 0.95,
                ["frequency_penalty"] = 0,
                ["presence_penalty"] = 0,
                ["snippy"] = new JsonObject { ["enabled"] = false },
                ["tools"] = Tools(AgentTools()),
                ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            var stopwatch = Stopwatch.StartNew();
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            cli.Record("POST", "/v1/chat/completions", model, (int)response.StatusCode, _turns);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"bridge answered {(int)response.StatusCode}: {reader.ReadToEnd()}");
            }

            var completion = ReadStream(reader);
            _apiDurationMs += stopwatch.ElapsedMilliseconds;
            return completion;
        }

        /// <summary>Parses the SSE chunk stream: content deltas, tool-call deltas keyed by index, <c>finish_reason</c>, the usage chunk, <c>[DONE]</c>.</summary>
        private static Completion ReadStream(StreamReader reader)
        {
            var content = new StringBuilder();
            var toolCalls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
            var done = false;
            while (!done && reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data: ".Length..].Trim();
                if (data == "[DONE]")
                {
                    done = true;
                    break;
                }

                var chunk = JsonNode.Parse(data) as JsonObject;
                foreach (var choiceNode in chunk?["choices"] as JsonArray ?? [])
                {
                    if (choiceNode is not JsonObject choice || choice["delta"] is not JsonObject delta)
                    {
                        continue;
                    }

                    if (delta["content"] is JsonValue text && text.TryGetValue<string>(out var piece))
                    {
                        content.Append(piece);
                    }

                    foreach (var callNode in delta["tool_calls"] as JsonArray ?? [])
                    {
                        if (callNode is not JsonObject call)
                        {
                            continue;
                        }

                        var index = call["index"] is JsonValue i && i.TryGetValue<int>(out var n) ? n : toolCalls.Count;
                        if (!toolCalls.TryGetValue(index, out var entry))
                        {
                            entry = ("", "", new StringBuilder());
                        }

                        var id = call["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) && idText.Length > 0 ? idText : entry.Id;
                        var function = call["function"] as JsonObject;
                        var name = function?["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var nameText) && nameText.Length > 0 ? nameText : entry.Name;
                        if (function?["arguments"] is JsonValue argsValue && argsValue.TryGetValue<string>(out var argsText))
                        {
                            entry.Arguments.Append(argsText);
                        }

                        toolCalls[index] = (id, name, entry.Arguments);
                    }
                }
            }

            if (!done)
            {
                throw new InvalidOperationException("the bridge closed the stream without [DONE]");
            }

            return new Completion(
                content.ToString(),
                toolCalls.Values.Select(entry => new StreamedToolCall(entry.Id.Length > 0 ? entry.Id : Guid.NewGuid().ToString("N"), entry.Name, entry.Arguments.ToString())).ToList());
        }

        private static JsonObject ParseArguments(string arguments)
        {
            try
            {
                return JsonNode.Parse(arguments.Length > 0 ? arguments : "{}") as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject { ["_raw"] = arguments };
            }
        }

        /// <summary>
        /// The tools the selected agent may use, from the <c>tools:</c> list of its front matter (VS Code tool ids:
        /// <c>execute/runInTerminal</c> or <c>execute</c> is <c>bash</c>, <c>read/readFile</c> or <c>read</c> is
        /// <c>view</c>, <c>edit/createFile</c> is <c>create</c>, <c>edit/editFiles</c> or <c>edit</c> is <c>edit</c> and
        /// <c>create</c>; <c>skill</c> is always offered), as the live CLI restricts them; every tool without an agent
        /// or a <c>tools:</c> entry.
        /// </summary>
        private IReadOnlySet<string>? AgentTools()
        {
            if (agent is null || AgentFrontmatter(agent) is not { } frontmatter)
            {
                return null;
            }

            var ids = ToolIds(frontmatter);
            if (ids is null)
            {
                return null;
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "skill" };
            foreach (var id in ids)
            {
                switch (id)
                {
                    case "execute" or "execute/runInTerminal":
                        allowed.Add("bash");
                        break;
                    case "read" or "read/readFile":
                        allowed.Add("view");
                        break;
                    case "edit":
                        allowed.Add("create");
                        allowed.Add("edit");
                        break;
                    case "edit/createFile" or "edit/createDirectory":
                        allowed.Add("create");
                        break;
                    case "edit/editFiles":
                        allowed.Add("edit");
                        break;
                }
            }

            return allowed;
        }

        /// <summary>The tool definitions on the wire (OpenAI function tools with the real CLI's argument names), cut to <paramref name="allowed"/> when given.</summary>
        private static JsonArray Tools(IReadOnlySet<string>? allowed) =>
            new(AllTools().Where(tool => allowed is null || allowed.Contains(tool["function"]!["name"]!.GetValue<string>())).ToArray());

        private static IEnumerable<JsonObject> AllTools() =>
        [
            Tool("bash", "Run a bash command in the workspace.", new JsonObject
            {
                ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The Bash command and arguments to run." },
                ["description"] = new JsonObject { ["type"] = "string", ["description"] = "A short human-readable description of what the command does." },
            }, ["command", "description"]),
            Tool("view", "View a file or directory.", new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Full absolute path to file or directory. File MUST exist to view." },
            }, ["path"]),
            Tool("create", "Create a file.", new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Full absolute path to file to create." },
                ["file_text"] = new JsonObject { ["type"] = "string", ["description"] = "The content of the file to be created." },
            }, ["path", "file_text"]),
            Tool("edit", "Replace a string in a file.", new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Full absolute path to file to edit. File MUST exist to edit." },
                ["old_str"] = new JsonObject { ["type"] = "string", ["description"] = "The string in the file to replace." },
                ["new_str"] = new JsonObject { ["type"] = "string", ["description"] = "The new string to replace old_str with." },
            }, ["path"]),
            Tool("skill", "Load a skill by name.", new JsonObject
            {
                ["skill"] = new JsonObject { ["type"] = "string", ["description"] = "The skill name to invoke. E.g., \"pdf\" or \"code-reviewer\"" },
            }, ["skill"]),
        ];

        private static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
                },
            },
        };

        /// <summary>
        /// The system prompt in the real CLI's shape where the scorers look: the non-interactive preamble, the
        /// workspace's <c>copilot-instructions.md</c> in a <c>custom_instruction</c> block, the instruction-file table,
        /// the <c>available_skills</c> block, and the selected agent's body.
        /// </summary>
        private string SystemPrompt()
        {
            var prompt = new StringBuilder();
            prompt.Append("You are the GitHub Copilot CLI, a terminal assistant built by GitHub. You are running in non-interactive mode and have no way to communicate with the user; complete the task with the tools available and then reply with a summary.\n");
            prompt.Append("<workspace>The current working directory is ").Append(cwd).Append(".</workspace>\n\n");

            var custom = ReadWorkspace(".github/copilot-instructions.md");
            if (custom is not null)
            {
                prompt.Append("<custom_instruction>\n").Append(custom.Trim()).Append("\n</custom_instruction>\n\n");
            }

            var instructionsDir = sandbox.HostPath(Combine(cwd, ".github/instructions"));
            if (Directory.Exists(instructionsDir))
            {
                prompt.Append("Here is a list of instruction files with the file patterns they apply to; view a file before creating or editing anything it matches.\n");
                prompt.Append("| Pattern | File Path | Description |\n|---|---|---|\n");
                foreach (var file in Directory.EnumerateFiles(instructionsDir, "*.instructions.md").Order(StringComparer.Ordinal))
                {
                    var (frontmatter, _) = SplitFrontmatter(File.ReadAllText(file));
                    prompt.Append("| ").Append(Match(ApplyToPattern(), frontmatter) ?? "**").Append(" | .github/instructions/").Append(Path.GetFileName(file))
                        .Append(" | ").Append(Match(DescriptionPattern(), frontmatter) ?? "").Append(" |\n");
                }

                prompt.Append('\n');
            }

            if (_skills.Count > 0)
            {
                prompt.Append("<available_skills>\n");
                foreach (var skill in _skills)
                {
                    prompt.Append("<skill><name>").Append(skill.Name).Append("</name><description>").Append(skill.Description).Append("</description><location>plugin</location></skill>\n");
                }

                prompt.Append("</available_skills>\n\n");
            }

            if (agent is not null)
            {
                // The live 1.0.83 block (scratchpad review-system.txt): an <agent_instructions> element with this preamble, then the body.
                var body = AgentBody(agent) ?? throw new InvalidOperationException($"No such agent: {agent}");
                prompt.Append("<agent_instructions>\nThe following instructions come from the selected agent's configuration. Follow them while completing the user's task, but treat them as subordinate to the organization, safety, and runtime instructions above.\n\n")
                    .Append(body.Trim()).Append("\n</agent_instructions>\n");
            }

            return prompt.ToString();
        }

        /// <summary>Reads the plugin directories: every <c>SKILL.md</c> is a skill, every <c>*.prompt.md</c> a prompt command named <c>&lt;file&gt;.prompt</c>.</summary>
        private void LoadPlugins()
        {
            foreach (var pluginDir in pluginDirs)
            {
                var host = sandbox.HostPath(Combine(cwd, pluginDir));
                if (!Directory.Exists(host))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(host, "SKILL.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var (frontmatter, _) = SplitFrontmatter(File.ReadAllText(file));
                    var directory = Path.GetDirectoryName(file)!;
                    _skills.Add(new PluginSkill(Match(NamePattern(), frontmatter) ?? Path.GetFileName(directory), Match(DescriptionPattern(), frontmatter) ?? "", file, directory));
                }

                foreach (var file in Directory.EnumerateFiles(host, "*.prompt.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var (frontmatter, _) = SplitFrontmatter(File.ReadAllText(file));
                    var name = Path.GetFileName(file)[..^".md".Length];
                    _skills.Add(new PluginSkill(name, Match(DescriptionPattern(), frontmatter) ?? "", file, Path.GetDirectoryName(file)!));
                }
            }
        }

        private string? AgentBody(string qualifiedName) => AgentFile(qualifiedName) is { } file ? SplitFrontmatter(File.ReadAllText(file)).Body : null;

        private string? AgentFrontmatter(string qualifiedName) => AgentFile(qualifiedName) is { } file ? SplitFrontmatter(File.ReadAllText(file)).Frontmatter : null;

        private string? AgentFile(string qualifiedName)
        {
            var id = qualifiedName.Contains(':') ? qualifiedName[(qualifiedName.IndexOf(':') + 1)..] : qualifiedName;
            foreach (var pluginDir in pluginDirs)
            {
                var host = sandbox.HostPath(Combine(cwd, pluginDir));
                if (!Directory.Exists(host))
                {
                    continue;
                }

                var file = Directory.EnumerateFiles(host, id + ".agent.md", SearchOption.AllDirectories).FirstOrDefault();
                if (file is not null)
                {
                    return file;
                }
            }

            return null;
        }

        /// <summary>Executes one tool call against the mirror directory; the third element is a user message to inject after the result (the skill context).</summary>
        private (bool Success, string Content, string? Inject) Execute(string name, JsonObject arguments)
        {
            try
            {
                switch (name)
                {
                    case "bash":
                        return Bash(Str(arguments["command"]) ?? "");
                    case "view":
                    {
                        var path = HostPath(Str(arguments["path"]) ?? "");
                        if (Directory.Exists(path))
                        {
                            return (true, string.Join("\n", Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Order(StringComparer.Ordinal)), null);
                        }

                        return File.Exists(path) ? (true, File.ReadAllText(path), null) : (false, $"File not found: {Str(arguments["path"])}", null);
                    }

                    case "create":
                    {
                        var path = HostPath(Str(arguments["path"]) ?? "");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, Str(arguments["file_text"]) ?? "");
                        _filesModified.Add(Str(arguments["path"]) ?? "");
                        return (true, $"File created successfully at: {Str(arguments["path"])}", null);
                    }

                    case "edit":
                    {
                        var path = HostPath(Str(arguments["path"]) ?? "");
                        if (!File.Exists(path))
                        {
                            return (false, $"File not found: {Str(arguments["path"])}", null);
                        }

                        var text = File.ReadAllText(path);
                        var oldText = Str(arguments["old_str"]) ?? "";
                        var index = text.IndexOf(oldText, StringComparison.Ordinal);
                        if (oldText.Length == 0 || index < 0)
                        {
                            return (false, "No replacement was performed, old_str did not appear verbatim in the file.", null);
                        }

                        File.WriteAllText(path, text[..index] + (Str(arguments["new_str"]) ?? "") + text[(index + oldText.Length)..]);
                        _filesModified.Add(Str(arguments["path"]) ?? "");
                        return (true, $"The file {Str(arguments["path"])} has been edited.", null);
                    }

                    case "skill":
                    {
                        var wanted = Str(arguments["skill"]) ?? "";
                        var skill = _skills.FirstOrDefault(s => s.Name == wanted || "hve-core:" + s.Name == wanted);
                        if (skill is null)
                        {
                            return (false, $"Skill \"{wanted}\" not found. Available: {string.Join(", ", _skills.Select(s => s.Name))}", null);
                        }

                        var (_, body) = SplitFrontmatter(File.ReadAllText(skill.Path));
                        var related = Directory.EnumerateFiles(skill.BaseDirectory, "*", SearchOption.AllDirectories)
                            .Where(file => file != skill.Path)
                            .Select(file => Path.GetRelativePath(skill.BaseDirectory, file).Replace('\\', '/'))
                            .Order(StringComparer.Ordinal)
                            .ToList();
                        var context = new StringBuilder();
                        context.Append("<skill-context name=\"").Append(skill.Name).Append("\">\nBase directory for this skill: ").Append(skill.BaseDirectory).Append('\n');
                        if (related.Count > 0)
                        {
                            context.Append("Related files (use view tool to read): ").Append(string.Join(", ", related)).Append('\n');
                        }

                        context.Append('\n').Append(body).Append("\n</skill-context>");
                        return (true, $"Skill \"{skill.Name}\" loaded successfully. Follow the instructions in the skill context that follows.", context.ToString());
                    }

                    default:
                        return (false, $"Unknown tool '{name}'.", null);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return (false, $"{name} failed: {ex.Message}", null);
            }
        }

        private (bool Success, string Content, string? Inject) Bash(string command)
        {
            var id = _shellId++;
            var hostCwd = sandbox.HostPath(cwd);
            Directory.CreateDirectory(hostCwd);
            var startInfo = new ProcessStartInfo("bash")
            {
                WorkingDirectory = hostCwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            // The model's command names sandbox paths (/workspace/..., the plugin under /opt); the mirror plays them.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(sandbox.MapCommandText(command));
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("bash did not start");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill(entireProcessTree: true);
                return (false, $"<shellId: {id} timed out after {ToolTimeout.TotalSeconds}s>", null);
            }

            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return (true, $"{output}\n<shellId: {id} completed with exit code {process.ExitCode}>", null);
        }

        private string HostPath(string path) => sandbox.HostPath(Combine(cwd, path));

        private string? ReadWorkspace(string relative)
        {
            var host = sandbox.HostPath(Combine(cwd, relative));
            return File.Exists(host) ? File.ReadAllText(host) : null;
        }

        private static string Combine(string cwd, string path) => path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path;

        private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private sealed record StreamedToolCall(string Id, string Name, string Arguments);

    private sealed record Completion(string Content, IReadOnlyList<StreamedToolCall> ToolCalls);

    private void Record(string method, string path, string model, int status, int turn)
    {
        if (_environment() is not { } environment)
        {
            return;
        }

        var line = new JsonObject { ["method"] = method, ["path"] = path, ["model"] = model, ["status"] = status, ["turn"] = turn }.ToJsonString() + "\n";
        var existing = environment.FileText(RequestLog) ?? "";
        environment.WriteFileAsync(RequestLog, existing + line).GetAwaiter().GetResult();
    }
}
