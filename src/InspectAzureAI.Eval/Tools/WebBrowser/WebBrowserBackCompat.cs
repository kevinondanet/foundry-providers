using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tools/_web_browser/_back_compat.py</c>: the "old" client for the deprecated
/// <c>aisiuk/inspect-web-browser-tool</c> image, which exposes the browser through two Python scripts in
/// the container rather than the JSON-RPC service. <see cref="WebBrowser.WebBrowserCmdAsync"/> falls back
/// to it when the sample has that image but not <c>inspect-tool-support</c>.
/// </summary>
public static partial class WebBrowserBackCompat
{
    /// <summary>Port of <c>INSPECT_WEB_BROWSER_IMAGE_DOCKERHUB_DEPRECATED</c> (<c>util/_sandbox/docker/internal.py</c>).</summary>
    public const string InspectWebBrowserImageDockerHubDeprecated = "aisiuk/inspect-web-browser-tool";

    public const string WebClientRequest = "/app/web_browser/web_client.py";

    public const string WebClientNewSession = "/app/web_browser/web_client_new_session.py";

    /// <summary>The once-only deprecation warning (Python's <c>warn_once</c> text).</summary>
    public const string DeprecationWarning =
        "WARNING: Use of the `aisiuk/inspect-web-browser-tool` image is deprecated. Please update your configuration to use the `aisiuk/inspect-tool-support` image or install the `inspect-tool-support` package into your own image.";

    /// <summary>Python: <c>timeout=180</c> on both exec calls.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Port of <c>old_web_browser_cmd(cmd, *args)</c>: creates the session on first use (the session id is
    /// kept in the un-instanced <see cref="WebBrowserStore"/>, as in Python), runs the client script and
    /// parses its field-per-line output. A failed script is an <see cref="InvalidOperationException"/>
    /// (Python's <c>RuntimeError</c>); an <c>error</c> field is a <see cref="ToolError"/>.
    /// </summary>
    public static async Task<ToolResult> OldWebBrowserCmdAsync(string cmd, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(args);
        var sandbox = await WebBrowserSandboxAsync(cancellationToken).ConfigureAwait(false);
        ProviderLogger.WarnOnce(DeprecationWarning);

        var store = Store.StoreAs<WebBrowserStore>();
        if (store.SessionId.Length == 0)
        {
            var result = await sandbox.ExecAsync(["python3", WebClientNewSession], timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Error creating new web browser session: {result.Stderr}");
            }

            store.SessionId = result.Stdout.Trim('\n');
        }

        var sessionFlag = $"--session_name={store.SessionId}";
        var argList = new List<string> { "python3", WebClientRequest, sessionFlag, cmd };
        argList.AddRange(args);

        var response = await sandbox.ExecAsync(argList, timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException($"Error executing web browser command {cmd}({string.Join(", ", args)}): {response.Stderr}");
        }

        var parsed = ParseWebBrowserOutput(response.Stdout);
        if (parsed.TryGetValue("error", out var error) && error.Trim().Length > 0)
        {
            throw new ToolError(error);
        }

        if (parsed.ContainsKey("web_at"))
        {
            return WebBrowser.CrawlerResponse(Store.StoreAs<WebBrowserStore>(), parsed["main_content"], parsed["web_at"]);
        }

        throw new InvalidOperationException($"web_browser output must contain either 'error' or 'web_at' field: {response.Stdout}");
    }

    /// <summary>Port of <c>_web_browser_sandbox</c>: the sample sandbox holding the client script, else a <see cref="PrerequisiteError"/> with the deprecated compose guidance.</summary>
    public static async Task<ISandboxEnvironment> WebBrowserSandboxAsync(CancellationToken cancellationToken = default)
    {
        var sandbox = await SandboxWith.FindAsync(WebClientRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
        return sandbox ?? throw new PrerequisiteError(NotFoundMessage);
    }

    /// <summary>The <see cref="PrerequisiteError"/> text of <c>_web_browser_sandbox</c>.</summary>
    public static string NotFoundMessage { get; } =
        $"The web browser service was not found in any of the sandboxes for this sample. Please add the web browser service to your configuration. For example, the following Docker compose file uses the {InspectWebBrowserImageDockerHubDeprecated} image as its default sandbox:\n"
        + "\n"
        + "services:\n"
        + "  default:\n"
        + $"    image: \"{InspectWebBrowserImageDockerHubDeprecated}\"\n"
        + "    init: true\n"
        + "\n"
        + "Alternatively, this Docker compose file creates a dedicated image for the web browser service:\n"
        + "\n"
        + "services:\n"
        + "  default:\n"
        + "    image: \"python:3.12-bookworm\"\n"
        + "    init: true\n"
        + "    command: \"tail -f /dev/null\"\n"
        + "\n"
        + "  web_browser:\n"
        + $"    image: \"{InspectWebBrowserImageDockerHubDeprecated}\"\n"
        + "    init: true";

    /// <summary>
    /// Port of <c>_parse_web_browser_output</c>: the script prints <c>field: value</c> lines for
    /// <c>error</c>, <c>main_content</c>, <c>web_at</c>, <c>web_url</c> and <c>info</c>; lines that do not
    /// start a field continue the active one. Every field is present (empty when not printed).
    /// </summary>
    public static Dictionary<string, string> ParseWebBrowserOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var response = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web_url"] = "",
            ["main_content"] = "",
            ["web_at"] = "",
            ["info"] = "",
            ["error"] = "",
        };
        string? activeField = null;
        var activeFieldLines = new List<string>();

        void CollectActiveField()
        {
            if (activeField is not null)
            {
                response[activeField] = string.Join("\n", activeFieldLines);
            }

            activeFieldLines.Clear();
        }

        foreach (var line in SplitLines(output))
        {
            var fieldMatch = FieldRegex().Match(line);
            if (fieldMatch.Success)
            {
                CollectActiveField();
                activeField = fieldMatch.Groups[1].Value;
                activeFieldLines.Add(fieldMatch.Groups[2].Value);
            }
            else
            {
                activeFieldLines.Add(line);
            }
        }

        CollectActiveField();
        return response;
    }

    /// <summary>Python's <c>str.splitlines()</c> over the newline forms the script can emit (no trailing empty line).</summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    [GeneratedRegex(@"^(error|main_content|web_at|web_url|info)\s*:\s*(.+)$")]
    private static partial Regex FieldRegex();
}
