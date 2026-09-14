namespace InspectAzureAI.Swe.CodexCli;

/// <summary>How a failed Codex CLI launch is reported (<c>_codex_cli/codex_cli.py:401-404</c>; Codex has no refusal or uncaught-error retry).</summary>
public static class CodexCliExit
{
    /// <summary>Python's <c>RuntimeError</c> text: <c>Error executing codex cli agent {code}: {stdout}\n{stderr}</c>.</summary>
    public static string ErrorMessage(int exitCode, string stdout, string stderr) =>
        $"Error executing codex cli agent {exitCode}: {stdout}\n{stderr}";
}
