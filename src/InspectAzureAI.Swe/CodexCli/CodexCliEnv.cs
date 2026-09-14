namespace InspectAzureAI.Swe.CodexCli;

/// <summary>The Codex CLI environment of inspect_swe <c>_codex_cli/codex_cli.py:346-351</c>.</summary>
public static class CodexCliEnv
{
    /// <summary>
    /// In order: <c>CODEX_HOME</c>, <c>OPENAI_API_KEY</c> (the bridge token), <c>OPENAI_BASE_URL</c>
    /// (<c>{bridgeBaseUrl}/v1</c>) and <c>RUST_LOG=warning</c>; then the caller's variables, which overwrite a default in
    /// place and append otherwise (deviation D-X1: Python points at <c>localhost:{port}</c> with the literal key
    /// <c>api-key</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(string codexHome, string bridgeBaseUrl, string authToken, IReadOnlyDictionary<string, string>? env)
    {
        ArgumentNullException.ThrowIfNull(codexHome);
        ArgumentNullException.ThrowIfNull(bridgeBaseUrl);
        ArgumentNullException.ThrowIfNull(authToken);
        var result = new OrderedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEX_HOME"] = codexHome,
            [CodexCliConfig.ApiKeyEnvVar] = authToken,
            ["OPENAI_BASE_URL"] = $"{bridgeBaseUrl}/v1",
            ["RUST_LOG"] = "warning",
        };
        foreach (var (name, value) in env ?? new Dictionary<string, string>())
        {
            result[name] = value;
        }

        return result;
    }
}
