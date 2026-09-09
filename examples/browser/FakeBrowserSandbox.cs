using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Browser;

/// <summary>
/// The sandbox behind <c>--sandbox fake</c> for <c>examples/browser/browser.py</c>: a <see cref="FakeSandboxScript"/>
/// that plays the <c>aisiuk/inspect-tool-support</c> image. <c>which inspect-tool-support</c> finds the CLI, and
/// <c>inspect-tool-support exec</c> answers the JSON-RPC requests the <c>web_browser</c> tools send on stdin:
/// <c>version</c>, <c>web_new_session</c>, and the navigation methods with a <c>CrawlerResult</c> carrying a
/// hand-written web accessibility tree of www.aisi.gov.uk (the home page, or the About page after the About link is
/// clicked or its URL requested). The real container runs Playwright and Chromium; nothing here touches the network.
/// </summary>
internal static class FakeBrowserSandbox
{
    public const string HomeUrl = "https://www.aisi.gov.uk/";

    public const string AboutUrl = "https://www.aisi.gov.uk/about";

    /// <summary>The element id of the "About" link on the fake home page.</summary>
    public const int AboutLinkId = 12;

    public const string SessionName = "aisi-session";

    public const string Version = "1.2.3";

    /// <summary>The web accessibility tree of the fake home page (with an image data URL the tool strips).</summary>
    public static readonly string HomeTree =
        "[1] RootWebArea \"AI Security Institute\" [focused: True, url: https://www.aisi.gov.uk/]\n"
        + "  [5] link \"Home\" [url: https://www.aisi.gov.uk/]\n"
        + $"  [{AboutLinkId}] link \"About\" [url: {AboutUrl}]\n"
        + "  [14] link \"Research\" [url: https://www.aisi.gov.uk/research]\n"
        + "  [16] link \"Careers\" [url: https://www.aisi.gov.uk/careers]\n"
        + "  [20] img \"AISI logo\" [url: data:image/png;base64,iVBORw0KGgo=]\n"
        + "  [22] heading \"Making AI safe for everyone\"\n"
        + "  [24] StaticText \"The AI Security Institute is a directorate of the UK Department for Science, Innovation and Technology.\"\n"
        + "  [30] button \"Accept cookies\"";

    /// <summary>The web accessibility tree of the fake About page.</summary>
    public static readonly string AboutTree =
        "[1] RootWebArea \"About the AI Security Institute\" [focused: True, url: https://www.aisi.gov.uk/about]\n"
        + "  [5] link \"Home\" [url: https://www.aisi.gov.uk/]\n"
        + $"  [{AboutLinkId}] link \"About\" [url: {AboutUrl}]\n"
        + "  [40] heading \"About the AI Security Institute\"\n"
        + "  [42] StaticText \"The AI Security Institute (AISI) is a research organisation within the UK government.\"\n"
        + "  [44] StaticText \"We conduct pre-deployment evaluations of frontier AI systems, study misuse risks in cyber, chemical and biological domains, and research safeguards and agent autonomy.\"\n"
        + "  [46] StaticText \"We work with AI developers, international partners and the wider research community.\"";

    /// <summary>The main content the crawler extracts from the About page.</summary>
    public const string AboutMainContent =
        "The AI Security Institute (AISI) is a research organisation within the UK government. "
        + "We conduct pre-deployment evaluations of frontier AI systems, study misuse risks in cyber, chemical and biological domains, and research safeguards and agent autonomy. "
        + "We work with AI developers, international partners and the wider research community.";

    /// <summary>The script: the CLI on PATH, and every JSON-RPC request answered by <see cref="Answer"/>.</summary>
    public static FakeSandboxScript Create() => new FakeSandboxScript()
        .OnExact(FakeSandboxScript.Ok("/usr/local/bin/inspect-tool-support\n"), "which", LegacyToolSupport.LegacySandboxCli)
        .OnExact(Answer, LegacyToolSupport.LegacySandboxCli, "exec")
        .WithDefault(FakeSandboxScript.Fail(127, "command not found\n"));

    /// <summary>The JSON-RPC methods of every request answered so far (for tests): parsed from the recorded calls.</summary>
    public static IReadOnlyList<string> Methods(FakeSandboxScript script) =>
        script.Calls
            .Where(call => call.Cmd.Count == 2 && call.Cmd[0] == LegacyToolSupport.LegacySandboxCli && call.Cmd[1] == "exec" && call.Input is not null)
            .Select(call => JsonNode.Parse(call.Input!)!["method"]!.GetValue<string>())
            .ToList();

    /// <summary>Answers one <c>inspect-tool-support exec</c> call from the JSON-RPC request on its stdin.</summary>
    private static ExecResult? Answer(FakeExecCall call)
    {
        if (call.Input is null || JsonNode.Parse(call.Input) is not JsonObject request)
        {
            return FakeSandboxScript.Fail(1, "expected a JSON-RPC request on stdin\n");
        }

        var id = request["id"]?.DeepClone() ?? JsonValue.Create(1);
        var method = request["method"]?.GetValue<string>() ?? "";
        var parameters = request["params"] as JsonObject;
        return method switch
        {
            "version" => Success(id, Version),
            "web_new_session" => Success(id, new JsonObject { ["session_name"] = SessionName }),
            "web_go" => Crawler(id, parameters?["url"]?.GetValue<string>() ?? HomeUrl),
            "web_click" => Crawler(id, parameters?["element_id"]?.GetValue<int>() == AboutLinkId ? AboutUrl : HomeUrl),
            "web_type_submit" or "web_type" or "web_scroll" or "web_back" or "web_forward" or "web_refresh" => Crawler(id, HomeUrl),
            _ => Error(id, -32601, "Method not found"),
        };
    }

    private static ExecResult Crawler(JsonNode id, string url)
    {
        var about = url.StartsWith(AboutUrl, StringComparison.OrdinalIgnoreCase);
        return Success(id, new JsonObject
        {
            ["web_url"] = about ? AboutUrl : HomeUrl,
            ["main_content"] = about ? AboutMainContent : null,
            ["web_at"] = about ? AboutTree : HomeTree,
            ["error"] = null,
        });
    }

    private static ExecResult Success(JsonNode id, JsonNode? result) =>
        FakeSandboxScript.Ok(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());

    private static ExecResult Error(JsonNode id, int code, string message) =>
        FakeSandboxScript.Ok(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString());
}
