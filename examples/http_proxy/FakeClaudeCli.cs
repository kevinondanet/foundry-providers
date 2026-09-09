using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Examples.HttpProxy;

/// <summary>
/// The stand-in for the <c>claude</c> CLI under <c>--fake</c>: answers the sandbox exec of the Claude Code launch the way
/// the real binary would from the outside. It reads the prompt from the argv and the bridge address and token from the
/// environment the agent hands the exec, then plays the agent's session against the real sandbox agent bridge:
/// its own model turns go to <c>POST /v1/messages</c> (Claude Code's dialect), it "writes" <see cref="ScriptPath"/>
/// into the sandbox, and the script's FutureModel request goes to <c>POST /v1/chat/completions</c> — the OpenAI
/// dialect that <c>remap.py</c> forwards to the bridge in the live setup — so the offline run exercises both bridge
/// routes and the scripted model for real. It prints the <c>stream-json</c> lines the agent parses and appends every
/// bridge request to <see cref="RequestLog"/> in the sample's fake sandbox so tests can read it back.
/// </summary>
public sealed class FakeClaudeCli(Func<ScriptedSandboxEnvironment?> environment)
{
    /// <summary>Where the image's npm install puts the CLI (answered to <c>which claude</c>).</summary>
    public const string BinaryPath = "/usr/local/bin/claude";

    /// <summary>The working directory of the image (<c>WORKDIR /workspace</c>).</summary>
    public const string WorkingDirectory = "/workspace";

    /// <summary>The script the stand-in agent writes.</summary>
    public const string ScriptPath = "/workspace/futuremodel_haiku.py";

    /// <summary>The JSON-lines request log the stand-in writes into the fake sandbox: one <c>{method, path, model, status}</c> per bridge request.</summary>
    public const string RequestLog = "/workspace/.fake-claude-requests.jsonl";

    /// <summary>The fake FutureModel model name (<c>remap.py</c>'s <c>FAKE_MODEL</c>).</summary>
    public const string FutureModel = "futuremodel-1";

    /// <summary>What the stand-in's script asks the FutureModel API for.</summary>
    public const string HaikuPrompt = "Write a haiku about coding.";

    /// <summary>
    /// The script as an agent would write it: the FutureModel URL, the key from the environment, and a plain
    /// <c>urllib</c> call — which honours <c>HTTPS_PROXY</c>, so in the container it goes through mitmproxy.
    /// </summary>
    public const string Script = """
        import json
        import os
        import urllib.request

        API_URL = "https://api.futuremodel.ai/v1/chat/completions"
        API_KEY = os.environ["FUTUREMODEL_API_KEY"]


        def generate(prompt: str) -> str:
            body = json.dumps({"model": "futuremodel-1", "messages": [{"role": "user", "content": prompt}]}).encode()
            request = urllib.request.Request(
                API_URL,
                data=body,
                headers={"Authorization": f"Bearer {API_KEY}", "Content-Type": "application/json"},
            )
            with urllib.request.urlopen(request) as response:
                return json.load(response)["choices"][0]["message"]["content"]


        if __name__ == "__main__":
            print(generate("Write a haiku about coding."))

        """;

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

    /// <summary>Plays the CLI for one launch: plan, write the script, run it (the FutureModel call), report — printed as stream-json.</summary>
    public ExecResult Run(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var env = call.Env ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!env.TryGetValue("ANTHROPIC_BASE_URL", out var baseUrl) || !env.TryGetValue("ANTHROPIC_AUTH_TOKEN", out var token))
        {
            return FakeSandboxScript.Fail(1, "fake claude: ANTHROPIC_BASE_URL and ANTHROPIC_AUTH_TOKEN must be set\n");
        }

        if (!env.ContainsKey("FUTUREMODEL_API_KEY"))
        {
            return FakeSandboxScript.Fail(1, "fake claude: FUTUREMODEL_API_KEY must be set\n");
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

            // the agent writes the script, then runs it: the script's request reaches the bridge's OpenAI route
            _environment()?.WriteFileAsync(ScriptPath, Script).GetAwaiter().GetResult();
            var haiku = Completions(client, token);
            conversation.Add(Message("user", "Script output:\n" + haiku));

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

        var text = Post(client, "/v1/messages", body, ("x-api-key", token), model);
        var content = JsonNode.Parse(text)?["content"] as JsonArray ?? throw new InvalidOperationException($"no content in bridge response: {text}");
        return string.Concat(content.Select(block => block?["type"]?.GetValue<string>() == "text" ? block["text"]?.GetValue<string>() ?? "" : ""));
    }

    /// <summary>
    /// The script's request as <c>remap.py</c> hands it to the bridge: <c>POST /v1/chat/completions</c> for
    /// <see cref="FutureModel"/>. Deviation: the live script sends <c>FUTUREMODEL_API_KEY</c>, which the in-container
    /// Python proxy ignores; the C# bridge requires its own token, so the stand-in presents that.
    /// </summary>
    private string Completions(HttpClient client, string token)
    {
        var body = new JsonObject
        {
            ["model"] = FutureModel,
            ["messages"] = new JsonArray(Message("user", HaikuPrompt)),
        };
        var text = Post(client, "/v1/chat/completions", body, ("Authorization", "Bearer " + token), FutureModel);
        return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"no choices in bridge response: {text}");
    }

    private string Post(HttpClient client, string path, JsonObject body, (string Name, string Value) header, string model)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(header.Name, header.Value);
        using var response = client.Send(request, HttpCompletionOption.ResponseContentRead);
        using var reader = new StreamReader(response.Content.ReadAsStream());
        var text = reader.ReadToEnd();
        Record("POST", path, model, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"bridge answered {(int)response.StatusCode} for {path}: {text}");
        }

        return text;
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
