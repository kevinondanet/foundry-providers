namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>
/// The environment of the Copilot CLI subprocess, the counterpart of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeEnv"/>
/// (inspect_swe <c>_claude_code/env.py</c>): the bring-your-own-key variables that point the CLI at the sandbox
/// agent bridge (wire facts from the 1.0.83 probe: the openai type appends <c>/chat/completions</c> to the base URL
/// and sends the key as a bearer; the anthropic type appends <c>/v1/messages</c> and sends it as <c>x-api-key</c>),
/// plus the hygiene variables that keep the CLI offline and its state inside the sample's working directory.
/// Caller values win, and a caller's <c>COPILOT_HOME</c> is the home every derived path (logs, OTEL file) uses.
/// </summary>
public static class CopilotCliEnv
{
    /// <summary>The per-sample home directory under the agent cwd: sessions, permissions, logs and the OTEL file.</summary>
    public const string HomeDirName = ".copilot";

    public const string OtelFileName = "otel.jsonl";

    public static string Home(string agentCwd)
    {
        ArgumentNullException.ThrowIfNull(agentCwd);
        return agentCwd.TrimEnd('/') + "/" + HomeDirName;
    }

    public static string LogDir(string agentCwd) => LogDirOf(Home(agentCwd));

    /// <summary>The CLI's log directory under a resolved home.</summary>
    public static string LogDirOf(string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return home.TrimEnd('/') + "/logs";
    }

    /// <summary>The home the CLI will actually use: a caller <c>COPILOT_HOME</c> in <paramref name="env"/> wins over the default under the agent cwd.</summary>
    public static string ResolveHome(string agentCwd, IReadOnlyDictionary<string, string>? env)
    {
        ArgumentNullException.ThrowIfNull(agentCwd);
        return env is not null && env.TryGetValue("COPILOT_HOME", out var home) && home.Length > 0 ? home : Home(agentCwd);
    }

    /// <summary>The <c>COPILOT_PROVIDER_TYPE</c> token of a provider.</summary>
    public static string ProviderType(CopilotCliProvider provider) => provider switch
    {
        CopilotCliProvider.OpenAI => "openai",
        CopilotCliProvider.Anthropic => "anthropic",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "unknown provider"),
    };

    /// <summary>The base URL the CLI is given: the bridge root plus <c>/v1</c> for openai (it appends <c>/chat/completions</c>), the bare root for anthropic (it appends <c>/v1/messages</c>).</summary>
    public static string ProviderBaseUrl(string bridgeBaseUrl, CopilotCliProvider provider)
    {
        ArgumentNullException.ThrowIfNull(bridgeBaseUrl);
        var root = bridgeBaseUrl.TrimEnd('/');
        return provider == CopilotCliProvider.OpenAI ? root + "/v1" : root;
    }

    /// <summary>Builds the subprocess environment; the result keeps a fixed source order.</summary>
    public static IReadOnlyDictionary<string, string> Build(
        string bridgeBaseUrl,
        string authToken,
        CopilotCliModels models,
        string agentCwd,
        CopilotCliProvider provider = CopilotCliProvider.OpenAI,
        CopilotCliPermission permission = CopilotCliPermission.Yolo,
        bool otel = false,
        IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentNullException.ThrowIfNull(bridgeBaseUrl);
        ArgumentNullException.ThrowIfNull(authToken);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(agentCwd);
        var home = ResolveHome(agentCwd, env);
        var result = new OrderedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["COPILOT_PROVIDER_TYPE"] = ProviderType(provider),
            ["COPILOT_PROVIDER_BASE_URL"] = ProviderBaseUrl(bridgeBaseUrl, provider),
            ["COPILOT_PROVIDER_API_KEY"] = authToken,
            ["COPILOT_MODEL"] = models.Presented,
            ["COPILOT_OFFLINE"] = "true",
            ["COPILOT_AUTO_UPDATE"] = "false",
            ["COPILOT_HOME"] = home,
        };
        if (permission == CopilotCliPermission.Yolo)
        {
            result["COPILOT_ALLOW_ALL"] = "true";
        }

        if (otel)
        {
            result["COPILOT_OTEL_ENABLED"] = "true";
            result["COPILOT_OTEL_EXPORTER_TYPE"] = "file";
            result["COPILOT_OTEL_FILE_EXPORTER_PATH"] = home + "/" + OtelFileName;
        }

        if (env is not null)
        {
            foreach (var (name, value) in env)
            {
                result[name] = value;
            }
        }

        return result;
    }
}
