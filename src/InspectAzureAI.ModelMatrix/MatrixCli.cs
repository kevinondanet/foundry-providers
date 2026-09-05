using Azure;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.SweShowcase;

namespace InspectAzureAI.ModelMatrix;

/// <summary>
/// The <c>model-matrix</c> console app: every deployment on the Foundry resource × one showcase task × one agent
/// (Claude Code by default), with a results table on the console and a JSON summary on disk. Exit codes follow
/// the showcase: 0 ok, 1 at least one deployment errored, 2 usage or missing prerequisite, 3 sign-in or runtime failure.
/// </summary>
internal static class MatrixCli
{
    public const string Help = """
        model-matrix — run a showcase task with an agent against every deployment on the Foundry resource

        usage:
          model-matrix [options]

        selection:
          --only a,b,c              run only these deployments (repeatable, case-insensitive)
          --skip a,b,c              leave these deployments out (repeatable)
          --format OpenAI,Anthropic keep only these ARM model formats (repeatable)
          --include-non-chat        also try deployments whose capabilities say chatCompletion=false
          --parallel N              deployments evaluated at the same time (default 1)

        eval (the showcase's run flags):
          --task <name>             hello-swe (default), pytest-fix or system-explorer
          --agent <name>            claude-code (default), mini-swe or basic
          --limit N                 samples per deployment (default 1)
          --sample-id ID            run only this sample id (repeatable; wins over --limit)
          --epochs N                run every sample N times
          --max-samples N           samples in flight per deployment (default 4)
          --attempts N              submissions the agent may make (default 1)
          --sandbox docker|local    docker (default) or local (demo only; --fake defaults to local)
          --log-dir DIR             where the per-deployment eval logs go (default logs)
          --no-cleanup              keep the sandbox containers / temp directories
          --max-tokens <n|none>     max_tokens sent (default: the provider's max_tokens())
          --reasoning-effort <lvl>  Inspect's reasoning_effort (none|minimal|low|medium|high|xhigh|max)
          --model-arg key=value     repeatable; the Python -M model args (JSON values are parsed)
          --fake                    a scripted model on a fixed offline catalog (agent defaults to mini-swe)
          --debug                   Claude Code debug capture and full exception traces

        output:
          --out FILE                JSON summary (default <log-dir>/<timestamp>_matrix_<task>.json)
          --markdown FILE           also write the table as Markdown
          --resume FILE             rerun only the deployments that errored in this previous matrix JSON and carry
                                    its other rows over (same task and agent; not with --only)
          --show FILE               print a saved matrix JSON (with --markdown: re-render its Markdown) without running

        environment:
          AZUREAI_BASE_URL (or AZURE_ENDPOINT_URL / AZUREAI_ENDPOINT_URL)   the Foundry endpoint
          AZUREAI_RESOURCE_ID                                              the resource's ARM id (skips discovery)
          AZURE_SUBSCRIPTION_ID                                            narrows the resource search

        Each deployment takes the route its ARM model format implies (Anthropic → the Messages API, everything else →
        the model-inference route). Authentication is Entra ID only: sign in with `az login` first. Exit codes:
        0 ok, 1 at least one deployment errored, 2 usage or missing prerequisite, 3 sign-in / Azure / runtime failure.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        var arguments = args.ToList();
        if (arguments.Count == 0 || arguments.Contains("--help") || arguments.Contains("-h") || arguments.Contains("help"))
        {
            output.WriteLine(Help);
            return 0;
        }

        MatrixOptions? options = null;
        try
        {
            options = MatrixOptions.Parse(arguments);
            if (options.ShowPath is { } showPath)
            {
                var saved = LoadMatrix(showPath, "--show");
                output.WriteLine($"matrix   : {showPath}");
                output.WriteLine($"task     : {saved.Task} (scorer {saved.Scorer}) × {saved.Agent}, sandbox {saved.Sandbox}, {saved.CapturedAt:yyyy-MM-dd HH:mm} UTC");
                output.WriteLine(saved.Resource is { } r ? $"resource : {r.Name} ({r.ResourceGroup}, {r.Location}) at {saved.Endpoint}" : $"endpoint : {saved.Endpoint}");
                output.WriteLine();
                output.Write(saved.RenderTable());
                if (options.MarkdownPath is { } markdown)
                {
                    WriteFile(markdown, saved.RenderMarkdown());
                    output.WriteLine($"Markdown written to {markdown}");
                }

                return saved.HasErrors ? 1 : 0;
            }

            var previous = options.ResumePath is { } resumePath ? LoadResume(resumePath, options) : null;
            if (previous is not null)
            {
                var retry = previous.Rows.Where(r => r.IsError).Select(r => r.Deployment).ToList();
                if (retry.Count == 0)
                {
                    output.WriteLine($"Nothing to resume: no errored rows in {options.ResumePath}");
                    return 0;
                }

                output.WriteLine($"Resuming {retry.Count} errored deployment(s) from {options.ResumePath}: {string.Join(", ", retry)}");
                options = options with { Only = retry };
            }

            var report = await new MatrixRunner(options, output).RunAsync(cancellationToken);
            if (previous is not null)
            {
                report = report.MergedInto(previous) with
                {
                    Config = new Dictionary<string, object?>(report.Config) { ["resumedFrom"] = Path.GetFullPath(options.ResumePath!) },
                };
            }

            output.WriteLine();
            output.Write(report.RenderTable());

            var outPath = options.OutPath ?? Path.Combine(options.Run.LogDir, $"{DateTime.Now:yyyy-MM-ddTHH-mm-ss}_matrix_{report.Task}.json");
            WriteFile(outPath, report.ToJson());
            output.WriteLine($"Matrix written to {outPath}");
            if (options.MarkdownPath is { } markdownPath)
            {
                WriteFile(markdownPath, report.RenderMarkdown());
                output.WriteLine($"Markdown written to {markdownPath}");
            }

            return report.HasErrors ? 1 : 0;
        }
        catch (UsageError ex)
        {
            Console.Error.WriteLine($"{ex.Message}\n\n{Help}");
            return 2;
        }
        catch (PrerequisiteError ex)
        {
            Console.Error.WriteLine(ProviderUtil.StripRichMarkup(ex.Message));
            return 2;
        }
        catch (Exception ex) when (Cli.IsSignInFailure(ex))
        {
            Console.Error.WriteLine($"Entra ID sign-in failed: {Cli.SignInFailureMessage(ex)}\n\n{Cli.LoginHint}");
            return 3;
        }
        catch (RequestFailedException ex)
        {
            Console.Error.WriteLine($"Azure request failed (HTTP {ex.Status}): {AzureAIModelApi.AzureErrorMessage(ex)}");
            return 3;
        }
        catch (SandboxUnavailableException ex)
        {
            Console.Error.WriteLine($"Sandbox unavailable: {ex.Message}");
            return 3;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Run failed: {(options?.Run.Debug == true ? ex.ToString() : $"{ex.GetType().Name}: {ex.Message}")}");
            return 3;
        }
    }

    private static MatrixReport LoadMatrix(string path, string flag)
    {
        if (!File.Exists(path))
        {
            throw new UsageError($"{flag}: {path} does not exist");
        }

        try
        {
            return MatrixReport.FromJson(File.ReadAllText(path));
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new UsageError($"{flag}: {path} is not a matrix JSON ({ex.Message})");
        }
    }

    /// <summary>The previous matrix to resume; it must be for the same task and agent, or the merged rows would mix results.</summary>
    private static MatrixReport LoadResume(string path, MatrixOptions options)
    {
        var previous = LoadMatrix(path, "--resume");
        if (previous.Task != options.Task || previous.Agent != options.Agent)
        {
            throw new UsageError($"--resume: {path} is a {previous.Task} × {previous.Agent} matrix; this run is {options.Task} × {options.Agent}");
        }

        return previous;
    }

    private static void WriteFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }
}
