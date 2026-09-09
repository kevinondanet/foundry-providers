using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Examples.EvalsInEval;

/// <summary>
/// The stand-in for the <c>claude</c> CLI under <c>--fake</c>: answers the sandbox exec of the Claude Code launch the way
/// the real binary would from the outside. It reads the prompt from the argv and the bridge address and token from the
/// environment the agent hands the exec, talks to the sandbox agent bridge over HTTP exactly as Claude Code does
/// (<c>POST /v1/messages</c> with <c>x-api-key</c>), so the offline run exercises the bridge and the scripted model for
/// real, and prints the <c>stream-json</c> lines the agent parses. Instead of running the two inner
/// <c>inspect eval</c> commands (Python inspect-ai in a container) it feeds the model canned copies of their output.
/// Every request is appended to <see cref="RequestLog"/> in the sample's fake sandbox so tests can read it back.
/// </summary>
public sealed class FakeClaudeCli(Func<ScriptedSandboxEnvironment?> environment)
{
    /// <summary>Where the image's npm install puts the CLI (answered to <c>which claude</c>).</summary>
    public const string BinaryPath = "/usr/local/bin/claude";

    /// <summary>The working directory of the image (<c>WORKDIR /workspace</c>).</summary>
    public const string WorkingDirectory = "/workspace";

    /// <summary>The JSON-lines request log the stand-in writes into the fake sandbox: one <c>{method, path, model, status}</c> per bridge request.</summary>
    public const string RequestLog = "/workspace/.fake-claude-requests.jsonl";

    /// <summary>What <c>inspect eval file_probe.py</c> prints at the end (abridged); fed to the model as command output.</summary>
    public const string FileProbeOutput = "inspect eval file_probe.py\n\nfile_probe (1 sample): anthropic/inspect\nincludes  accuracy: 1.0  stderr: 0.0\n";

    /// <summary>What <c>inspect eval bash_task.py</c> prints at the end (abridged); fed to the model as command output.</summary>
    public const string BashTaskOutput = "inspect eval bash_task.py\n\nbash_task (1 sample): anthropic/inspect\nincludes  accuracy: 1.0  stderr: 0.0\n";

    private readonly Func<ScriptedSandboxEnvironment?> _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Whether <paramref name="call"/> is the agent's launch of the CLI (<c>bash -c 'exec 0&lt;/dev/null; "$@"' bash claude ...</c>).</summary>
    public static bool IsLaunch(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Cmd.Count > 4 && call.Cmd[0] == "bash" && call.Cmd[2] == ClaudeCodeCommand.LaunchScript;
    }

    /// <summary>The prompt: the argument after the bare <c>--</c> (the last argument).</summary>
    public static string Prompt(IReadOnlyList<string> cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var separator = cmd.ToList().LastIndexOf("--");
        return separator >= 0 && separator + 1 < cmd.Count ? cmd[separator + 1] : cmd[^1];
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

    /// <summary>Plays the CLI for one launch: two bridged turns (the plan, then the answer over the canned eval output), printed as stream-json.</summary>
    public ExecResult Run(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var env = call.Env ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!env.TryGetValue("ANTHROPIC_BASE_URL", out var baseUrl) || !env.TryGetValue("ANTHROPIC_AUTH_TOKEN", out var token))
        {
            return FakeSandboxScript.Fail(1, "fake claude: ANTHROPIC_BASE_URL and ANTHROPIC_AUTH_TOKEN must be set\n");
        }

        var model = Flag(call.Cmd, "--model") ?? env.GetValueOrDefault("ANTHROPIC_MODEL") ?? "inspect";
        var system = Flag(call.Cmd, "--append-system-prompt");
        var prompt = Prompt(call.Cmd);
        var sessionId = Flag(call.Cmd, "--session-id") ?? Flag(call.Cmd, "--resume") ?? "fake-session";

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var lines = new List<string> { Line("system", ("subtype", "init"), ("session_id", sessionId), ("model", model), ("cwd", WorkingDirectory)) };
            var conversation = new JsonArray { Message("user", prompt) };

            // turn 1: the plan
            var plan = Messages(client, token, model, system, conversation);
            lines.Add(AssistantLine(plan));
            conversation.Add(Message("assistant", plan));

            // "running" the two inner evals: their output goes back to the model as the next user turn
            var commandOutput = "Command output:\n\n" + FileProbeOutput + "\n" + BashTaskOutput;
            conversation.Add(Message("user", commandOutput));

            // turn 2: the report
            var report = Messages(client, token, model, system, conversation);
            lines.Add(AssistantLine(report));
            lines.Add(Line("result", ("subtype", "success"), ("session_id", sessionId), ("result", report), ("is_error", false)));
            return FakeSandboxScript.Ok(string.Join("\n", lines) + "\n");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return FakeSandboxScript.Fail(1, $"fake claude: {ex.Message}\n");
        }
    }

    /// <summary>One synchronous <c>POST /v1/messages</c> (Claude Code's own dialect) returning the assistant text.</summary>
    private string Messages(HttpClient client, string token, string model, string? system, JsonArray conversation)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["messages"] = JsonNode.Parse(conversation.ToJsonString()),
        };
        if (system is not null)
        {
            body["system"] = system;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", token);
        using var response = client.Send(request, HttpCompletionOption.ResponseContentRead);
        using var reader = new StreamReader(response.Content.ReadAsStream());
        var text = reader.ReadToEnd();
        Record("POST", "/v1/messages", model, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"bridge answered {(int)response.StatusCode}: {text}");
        }

        var content = JsonNode.Parse(text)?["content"] as JsonArray ?? throw new InvalidOperationException($"no content in bridge response: {text}");
        return string.Concat(content.Select(block => block?["type"]?.GetValue<string>() == "text" ? block["text"]?.GetValue<string>() ?? "" : ""));
    }

    private void Record(string method, string path, string model, int status)
    {
        if (_environment() is not { } environment)
        {
            return;
        }

        var line = new JsonObject { ["method"] = method, ["path"] = path, ["model"] = model, ["status"] = status }.ToJsonString() + "\n";
        var existing = environment.FileText(RequestLog) ?? "";
        environment.WriteFileAsync(RequestLog, existing + line).GetAwaiter().GetResult();
    }

    private static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };

    private static string AssistantLine(string text) =>
        new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        }.ToJsonString();

    private static string Line(string type, params (string Key, object Value)[] fields)
    {
        var line = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields)
        {
            line[key] = value switch
            {
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                int number => JsonValue.Create(number),
                _ => JsonValue.Create(value.ToString() ?? ""),
            };
        }

        return line.ToJsonString();
    }
}
