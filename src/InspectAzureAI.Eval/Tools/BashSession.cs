using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tools/_bash_session.py</c> <c>bash_session()</c>: an interactive bash shell in a long
/// running session inside the sample sandbox. The shell lives in the injected <c>inspect-sandbox-tools</c>
/// server; each call is one JSON-RPC exchange, and the session name is kept in the sample
/// <see cref="Store"/> under the same keys as Python's <c>BashSessionStore</c>, so one bash process is
/// shared by every call with the same <c>instance</c>.
/// </summary>
public static class BashSession
{
    public const string Name = "bash_session";

    /// <summary>The tool description the model sees (Python's <c>execute</c> docstring, verbatim).</summary>
    public const string Description =
        "Interact with a bash shell.\n\n"
        + "Interact with a bash shell by sending it input text and retrieving output\n"
        + "from it. There is no guarantee that all output will be returned in a\n"
        + "single call. Call this function multiple times to retrieve additional\n"
        + "output from the shell.\n\n"
        + "USAGE NOTES:\n"
        + "- Ensure that the shell is at a command prompt (typically when the\n"
        + "  output ends in \"$ \" or \"# \") before submitting a new command.\n"
        + "- Control characters must be sent as Unicode escape sequences (e.g., use\n"
        + "  \"\\u0003\" for Ctrl+C/ETX, \"\\u0004\" for Ctrl+D/EOT). The literal string\n"
        + "  \"Ctrl+C\" will not be interpreted as a control character.\n"
        + "- Use the \"read\" action to retrieve output from the shell without\n"
        + "  sending any input. This is useful for long-running commands that\n"
        + "  produce output over time. The \"read\" action will return any new output\n"
        + "  since the last call.\n"
        + "- If a long-running command is in progress, additional input to execute\n"
        + "  a new command will not be processed until the previous completes. To\n"
        + "  abort a long-running command, use the \"interrupt\" action:\n"
        + "  `bash_session(action=\"interrupt\")`\n"
        + "- If output ends with \"> \" (the shell's continuation prompt), it means\n"
        + "  the previous input contained unmatched quotes, backticks, or other\n"
        + "  incomplete syntax. Either complete the quoted input or use the\n"
        + "  \"interrupt\" action to cancel, then retry with corrected input.\n\n"
        + "Example use case:\n"
        + "- For a short-running command with a nominal amount of output, a single\n"
        + "  call may suffice.\n"
        + "  ```\n"
        + "  bash_session(action=\"type_submit\", input=\"echo foo\") -> \"foo\\nuser@host:/# \"\n"
        + "  ```\n"
        + "- For a long-running command with output over time, multiple calls to are needed.\n"
        + "  ```\n"
        + "  bash_session(action=\"type_submit\", input=\"tail -f /tmp/foo.log\") -> <some output>\n"
        + "  bash_session(action=\"read\") -> <more output>\n"
        + "  # Send interrupt (Ctrl+C)\n"
        + "  bash_session(action=\"interrupt\") -> \"<final output>^Cuser@host:/# \"\n"
        + "  ```\n"
        + "- Interactive command awaiting more input from the user.\n"
        + "  ```\n"
        + "  bash_session(action=\"type_submit\", input=\"ssh fred@foo.com\") -> \"foo.com's password: \"\n"
        + "  bash_session(action=\"type_submit\", input=\"secret\") -> \"fred@foo.com:~$ \"\n"
        + "  ```";

    /// <summary>Python <c>DEFAULT_WAIT_FOR_OUTPUT</c>.</summary>
    public static readonly TimeSpan DefaultWaitForOutput = TimeSpan.FromSeconds(30);

    /// <summary>Python <c>DEFAULT_IDLE_TIME</c> (seconds, sent as a JSON number).</summary>
    public const double DefaultIdleTimeSeconds = 0.5;

    /// <summary>Python <c>TRANSPORT_TIMEOUT</c>: how long the basic RPC call overhead may take (some K8s deployments are very slow).</summary>
    public static readonly TimeSpan TransportTimeout = TimeSpan.FromSeconds(180);

    public static readonly IReadOnlyList<string> Actions = ["type", "type_submit", "restart", "read", "interrupt"];

    /// <summary>The parameter schema, byte-for-byte what Python's <c>ToolDef(bash_session()).parameters</c> dumps.</summary>
    public static ToolParams Parameters { get; } = new()
    {
        Properties = new Dictionary<string, ToolParam>
        {
            ["action"] = new()
            {
                Type = ["string"],
                Description =
                    "The action to execute:\n"
                    + "- \"type\": Send input without a return key\n"
                    + "- \"type_submit\": Send input followed by a return key\n"
                    + "- \"read\": Read any new output without sending input\n"
                    + "- \"interrupt\": Send a Ctrl+C (ETX character) to interrupt the current process\n"
                    + "- \"restart\": Restart the bash session",
                Enum = Actions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray(),
            },
            ["input"] = new()
            {
                Description =
                    "The input to send to the shell.\n"
                    + "Required for \"type\". Optional for \"type_submit\" actions. Must\n"
                    + "not be provided for \"restart\", \"read\", or \"interrupt\" actions.",
                AnyOf = [ToolParam.Of("string"), ToolParam.Of("null")],
            },
        },
        Required = ["action"],
    };

    /// <summary>Port of <c>bash_session_new_session</c>'s <c>NewSessionResult</c>.</summary>
    public sealed record NewSessionResult
    {
        [JsonPropertyName("session_name")]
        public required string SessionName { get; init; }
    }

    /// <summary>The store keys Python's <c>store_as(BashSessionStore, instance=...)</c> writes: <c>BashSessionStore[:{instance}]:{field}</c>.</summary>
    public static string StoreKey(string? instance, string field) =>
        instance is null ? $"BashSessionStore:{field}" : $"BashSessionStore:{instance}:{field}";

    /// <summary>
    /// Port of <c>bash_session(timeout, wait_for_output, user, instance)</c>.
    /// </summary>
    /// <param name="timeout">Timeout for the command; defaults to <paramref name="waitForOutput"/> plus <see cref="TransportTimeout"/>, and may not be less than that.</param>
    /// <param name="waitForOutput">Maximum time to wait for output (whole seconds); defaults to 30 seconds. With no output in that time the call returns an empty string.</param>
    /// <param name="user">Username to run commands as.</param>
    /// <param name="instance">Instance id (each unique instance id has its own bash process).</param>
    /// <param name="support">The injection facade; defaults to <see cref="SandboxToolSupport.Default"/>.</param>
    public static ToolDef Create(TimeSpan? timeout = null, TimeSpan? waitForOutput = null, string? user = null, string? instance = null, SandboxToolSupport? support = null)
    {
        var wait = waitForOutput ?? DefaultWaitForOutput;
        if (wait <= TimeSpan.Zero || wait.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException($"wait_for_output must be a positive whole number of seconds, but got {wait}.", nameof(waitForOutput));
        }

        var minTimeout = wait + TransportTimeout;
        var effectiveTimeout = timeout ?? minTimeout;
        if (effectiveTimeout < minTimeout)
        {
            throw new ArgumentException(
                $"Timeout must be at least {Seconds(minTimeout)} seconds, but got {Seconds(effectiveTimeout)}.",
                nameof(timeout));
        }

        var toolSupport = support ?? SandboxToolSupport.Default;
        var waitSeconds = (long)wait.TotalSeconds;
        return new ToolDef(Name, Description, Parameters, async (arguments, cancellationToken) =>
        {
            var action = ToolArguments.RequiredChoice(arguments, "action", Actions);
            var input = ToolArguments.OptionalString(arguments, "input");
            switch (action)
            {
                case "type" when input is null:
                    throw new ToolParsingError($"'input' is required for '{action}' action.");
                case "restart" or "read" or "interrupt" when input is not null:
                    throw new ToolParsingError($"Do not provide 'input' with '{action}' action.");
            }

            var store = SampleContext.Require().Store;
            var injected = await toolSupport.SandboxWithInjectedToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var transport = injected.Transport;

            // Python's StoreModel materialises every field on first access, the instance included.
            var instanceKey = StoreKey(instance, "instance");
            if (!store.Contains(instanceKey))
            {
                store.Set(instanceKey, instance);
            }

            var sessionKey = StoreKey(instance, "session_id");
            if (!store.Contains(sessionKey))
            {
                store.Set(sessionKey, "");
            }

            var sessionId = store.Get<string>(sessionKey, "");
            if (sessionId.Length == 0)
            {
                try
                {
                    var session = await JsonRpc.ExecModelRequestAsync<NewSessionResult>(
                        "bash_session_new_session",
                        user is null ? new JsonObject() : new JsonObject { ["user"] = user },
                        transport,
                        SandboxToolsErrorMapper.Instance,
                        injected.CallOptions(TransportTimeout),
                        cancellationToken).ConfigureAwait(false);
                    sessionId = session.SessionName;
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException("Timed out creating new session");
                }

                store.Set(sessionKey, sessionId);
            }

            var parameters = new JsonObject { ["session_name"] = sessionId };
            switch (action)
            {
                case "type":
                    parameters["input"] = input;
                    AddTiming(parameters, waitSeconds);
                    break;
                case "type_submit":
                    // Deviation: Python interpolates a missing input as the literal text "None\n"; a bare newline
                    // (just the return key) is sent instead, see docs/ports/sandbox-tools.md.
                    parameters["input"] = $"{input}\n";
                    AddTiming(parameters, waitSeconds);
                    break;
                case "interrupt":
                    parameters["input"] = "\u0003";
                    AddTiming(parameters, waitSeconds);
                    break;
                case "read":
                    AddTiming(parameters, waitSeconds);
                    break;
                case "restart":
                    parameters["restart"] = true;
                    break;
            }

            var output = await JsonRpc.ExecScalarRequestAsync<string>(
                Name,
                parameters,
                transport,
                SandboxToolsErrorMapper.Instance,
                injected.CallOptions(effectiveTimeout),
                cancellationToken).ConfigureAwait(false);
            return output;
        });
    }

    private static void AddTiming(JsonObject parameters, long waitSeconds)
    {
        parameters["wait_for_output"] = waitSeconds;
        parameters["idle_timeout"] = DefaultIdleTimeSeconds;
        parameters["max_output_bytes"] = SandboxLimits.MaxExecOutputSize;
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
