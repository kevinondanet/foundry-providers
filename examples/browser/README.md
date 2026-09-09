# Web Browser

A C# port of `examples/browser/browser.py` of the inspect_ai repository. One sample asks the model to navigate to https://www.aisi.gov.uk/ with the web browser tools, find the page that describes the work of the UK AI Security Institute, and summarise it in two paragraphs. The browser is a headless Chromium served by the `inspect-tool-support` service of the `aisiuk/inspect-tool-support` image (`compose.yaml`, copied verbatim), and the answer is scored with `includes()` (the sample has no target, so any completion is correct, as in Python).

What it demonstrates:

- `WebBrowser.Create()` (`web_browser()`): the eight tools `web_browser_go`, `web_browser_click`, `web_browser_type_submit`, `web_browser_type`, `web_browser_scroll`, `web_browser_back`, `web_browser_forward` and `web_browser_refresh`, each answering with the page's web accessibility tree (element ids in brackets) and, where the crawler finds one, the page's main content; none of them may run in parallel.
- The tools find the service on the sandbox's PATH (`LegacyToolSupport`), create one browser session per sample (`WebBrowserStore`) and talk JSON-RPC to it over `inspect-tool-support exec` (`SandboxJsonRpcTransport`).
- `Solvers.UseTools(...)` + `Solvers.Generate()` (`use_tools(web_browser())`, `generate()`) and a Docker Compose sandbox (`SandboxSpec("docker", "compose.yaml")`).

## Running it

### Offline

```bash
dotnet run --project examples -- browser --fake --sandbox fake
```

A scripted model (`FakeBrowserModel`) calls `web_browser_go("https://www.aisi.gov.uk/")`, reads the accessibility tree, clicks the "About" link with `web_browser_click`, and answers with a two-paragraph summary of the page's main content. The sandbox is a scripted `inspect-tool-support` service (`FakeBrowserSandbox`): `which inspect-tool-support` succeeds and `inspect-tool-support exec` answers the JSON-RPC requests (`version`, `web_new_session`, `web_go`, `web_click`, ...) with a hand-written accessibility tree of the site's home and About pages, so no container, browser or network is involved. Add `--display conversation` to watch the tool calls and the trees.

### Against a Foundry deployment

```bash
dotnet run --project examples -- browser --model <deployment>
```

Requirements: Docker (the `aisiuk/inspect-tool-support` image is pulled, about a gigabyte with Chromium; the container needs outbound internet to reach aisi.gov.uk), `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), and a tool-calling deployment. Vision is not needed: the tools return text. The sandbox is `--sandbox docker` with this folder's `compose.yaml` by default; `--sandbox none` is refused because the tools need a sandbox, and `--sandbox local` fails at the first tool call because `inspect-tool-support` is not on this host's PATH.

### The `inspectai` CLI

The task is marked `[Task("browser")]`, so the CLI can discover it in the built assembly, as `inspect eval browser.py` does for the Python module:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval browser \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## The task

```csharp
[Task("browser")]
public static EvalTask BrowserTask() => Build(new SandboxSpec("docker", Path.Combine(AppContext.BaseDirectory, "browser", "compose.yaml")));

public static EvalTask Build(SandboxSpec sandbox) => new()
{
    Name = "browser",
    Dataset = new MemoryDataset(
    [
        new Sample("Use the web browser tool to navigate to https://www.aisi.gov.uk/. Then, see if you can find a page on the site that describes the work of the UK AISI. Then, summarize this work in two paragraphs."),
    ]),
    Solver = Solvers.Chain(
        Solvers.UseTools(WebBrowser.Create()),
        Solvers.Generate()),
    Scorers = [Scorers.Includes()],
    Sandbox = sandbox,
};
```

## Deviations from Python

- Python's `sandbox="docker"` resolves the `compose.yaml` next to `browser.py`; here the compose file is passed explicitly as `SandboxSpec("docker", "<example dir>/compose.yaml")` by the runner and the `[Task]` method.
- The `--fake` scripted model (`web_browser_go`, then `web_browser_click` on the site's About link, then a two-paragraph summary) and the fake sandbox (a scripted `inspect-tool-support` JSON-RPC service serving a hand-written accessibility tree for the home and About pages) are additions for running offline; the Python example only runs through `inspect eval` against the real container and the live site.
- A message limit of 20 guards the fake run; the Python task has none.
- `--sandbox none` is refused (the `web_browser` tools need a sandbox), and `--sandbox local` fails at the first tool call with the tool's `PrerequisiteError` because `inspect-tool-support` is not on this host's PATH.
