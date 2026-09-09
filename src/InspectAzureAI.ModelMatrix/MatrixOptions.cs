using System.Globalization;
using InspectAzureAI.SweShowcase;

namespace InspectAzureAI.ModelMatrix;

/// <summary>
/// The matrix's own flags (which deployments, how many at once, where the summary goes) on top of the showcase's
/// shared run flags (<see cref="RunOptions"/>: task, agent, limits, sandbox, log dir, generation config, fake, debug).
/// </summary>
internal sealed record MatrixOptions
{
    public const string DefaultTask = "hello-swe";

    public const int DefaultParallel = 1;

    /// <summary>Immediate eval-set retries of a deployment whose eval errors (Python's <c>retry_attempts</c> defaults to 10; a matrix of possibly broken deployments keeps it small).</summary>
    public const int DefaultRetryAttempts = 2;

    public required RunOptions Run { get; init; }

    /// <summary>Deployment names to run (case-insensitive); empty means every deployment.</summary>
    public IReadOnlyList<string> Only { get; init; } = [];

    /// <summary>Deployment names to leave out.</summary>
    public IReadOnlyList<string> Skip { get; init; } = [];

    /// <summary>ARM model formats to keep (<c>OpenAI</c>, <c>Anthropic</c>, <c>DeepSeek</c>, …); empty means all.</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    /// <summary>Also try deployments whose capabilities say <c>chatCompletion=false</c> (image, parsing and embedding models).</summary>
    public bool IncludeNonChat { get; init; }

    /// <summary>Deployments evaluated at the same time.</summary>
    public int Parallel { get; init; } = DefaultParallel;

    /// <summary>Port of <c>eval_set(retry_attempts=)</c> per deployment: how many times an errored eval is re-queued, reusing its completed samples.</summary>
    public int RetryAttempts { get; init; } = DefaultRetryAttempts;

    /// <summary>Where the JSON summary goes; null picks <c>&lt;log-dir&gt;/&lt;timestamp&gt;_matrix_&lt;task&gt;.json</c>.</summary>
    public string? OutPath { get; init; }

    /// <summary>Optional Markdown table.</summary>
    public string? MarkdownPath { get; init; }

    /// <summary>A previous matrix JSON whose errored rows are rerun; its other rows are carried over.</summary>
    public string? ResumePath { get; init; }

    /// <summary>A saved matrix JSON to print (and, with <c>--markdown</c>, re-render) instead of running anything.</summary>
    public string? ShowPath { get; init; }

    public string Task => Run.Task ?? DefaultTask;

    /// <summary>Claude Code unless told otherwise; the offline mode cannot drive it, so <c>--fake</c> defaults to mini-swe-agent.</summary>
    public string Agent => Run.Agent ?? (Run.Fake ? AgentChoice.MiniSweName : AgentChoice.ClaudeCodeName);

    /// <summary>One sample per deployment unless <c>--limit</c> or <c>--sample-id</c> says otherwise.</summary>
    public int? Limit => Run.SampleIds.Count > 0 ? null : Run.Limit ?? 1;

    public string SandboxType => Run.Sandbox ?? (Run.Fake ? "local" : "docker");

    /// <summary>Removes every known flag from <paramref name="arguments"/>; anything left over is a usage error.</summary>
    public static MatrixOptions Parse(List<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var includeNonChat = arguments.Remove("--include-non-chat");
        var only = TakeList(arguments, "--only");
        var skip = TakeList(arguments, "--skip");
        var formats = TakeList(arguments, "--format");
        var parallel = TakeParallel(arguments);
        var retryAttempts = TakeRetryAttempts(arguments);
        var outPath = RunOptions.TakeOption(arguments, "--out");
        var markdownPath = RunOptions.TakeOption(arguments, "--markdown");
        var resumePath = RunOptions.TakeOption(arguments, "--resume");
        var showPath = RunOptions.TakeOption(arguments, "--show");
        var run = RunOptions.Parse(arguments);

        if (resumePath is not null && only.Count > 0)
        {
            throw new UsageError("--resume decides which deployments run (the errored rows of the previous matrix); it cannot be combined with --only");
        }

        if (run.Model is not null)
        {
            throw new UsageError("model-matrix runs every deployment on the resource; narrow it with --only, --skip or --format instead of --model");
        }

        if (run.Route is not null)
        {
            throw new UsageError("model-matrix picks each deployment's route (models, anthropic or responses) from its ARM model format and capabilities; --route is not accepted");
        }

        if (arguments.Count > 0)
        {
            throw new UsageError($"unknown argument(s): {string.Join(" ", arguments)}");
        }

        return new MatrixOptions
        {
            Run = run,
            Only = only,
            Skip = skip,
            Formats = formats,
            IncludeNonChat = includeNonChat,
            Parallel = parallel,
            RetryAttempts = retryAttempts,
            OutPath = outPath,
            MarkdownPath = markdownPath,
            ResumePath = resumePath,
            ShowPath = showPath,
        };
    }

    /// <summary>A repeatable, comma-separated list option.</summary>
    private static IReadOnlyList<string> TakeList(List<string> arguments, string name)
    {
        var items = new List<string>();
        while (RunOptions.TakeOption(arguments, name) is { } value)
        {
            items.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return items;
    }

    private static int TakeRetryAttempts(List<string> arguments)
    {
        var value = RunOptions.TakeOption(arguments, "--retry-attempts");
        if (value is null)
        {
            return DefaultRetryAttempts;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : throw new UsageError($"--retry-attempts expects a non-negative number, got '{value}'");
    }

    private static int TakeParallel(List<string> arguments)
    {
        var value = RunOptions.TakeOption(arguments, "--parallel");
        if (value is null)
        {
            return DefaultParallel;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new UsageError($"--parallel expects a positive number, got '{value}'");
    }
}
