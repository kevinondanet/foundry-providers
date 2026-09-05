using System.Globalization;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.SweShowcase;

/// <summary>
/// The flags of <c>run</c> (plus the model flags shared with the Sample app), stripped destructively from the
/// argument list before the command is dispatched; a bad value is a <see cref="UsageError"/>.
/// </summary>
internal sealed record RunOptions
{
    public const string DefaultLogDir = "logs";

    public const int DefaultMaxSamples = 4;

    public static readonly IReadOnlyList<string> SandboxTypes = ["docker", "local"];

    public static readonly IReadOnlyList<string> Routes = ["models", "anthropic"];

    public string? Task { get; init; }

    public string? Agent { get; init; }

    public string? Model { get; init; }

    public string? Route { get; init; }

    public int? Limit { get; init; }

    /// <summary>Sample ids as the runner compares them: ints when numeric, otherwise strings (the Python CLI's conversion).</summary>
    public IReadOnlyList<object> SampleIds { get; init; } = [];

    public int? Epochs { get; init; }

    public int MaxSamples { get; init; } = DefaultMaxSamples;

    public int Attempts { get; init; } = 1;

    /// <summary>"docker" or "local"; null picks docker, or local under <see cref="Fake"/>.</summary>
    public string? Sandbox { get; init; }

    public string LogDir { get; init; } = DefaultLogDir;

    public bool Cleanup { get; init; } = true;

    public int? MaxTokens { get; init; }

    /// <summary>Distinguishes <c>--max-tokens none</c> (send nothing) from an absent flag (the provider's max_tokens()).</summary>
    public bool MaxTokensSet { get; init; }

    public string? ReasoningEffort { get; init; }

    public IReadOnlyDictionary<string, object?> ModelArgs { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public bool Fake { get; init; }

    public bool Debug { get; init; }

    /// <summary>The generation config the flags describe; a null MaxTokens lets <c>Model</c> fall back to the provider's max_tokens().</summary>
    public GenerateConfig GenerateConfig => new() { MaxTokens = MaxTokensSet ? MaxTokens : null, ReasoningEffort = ReasoningEffort };

    /// <summary>Removes every known flag from <paramref name="arguments"/>, leaving the command and its positional arguments.</summary>
    public static RunOptions Parse(List<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var fake = arguments.Remove("--fake");
        var debug = arguments.Remove("--debug");
        var noCleanup = arguments.Remove("--no-cleanup");
        var task = TakeOption(arguments, "--task");
        var agent = TakeOption(arguments, "--agent");
        var model = TakeOption(arguments, "--model");
        var route = TakeOption(arguments, "--route")?.Trim().ToLowerInvariant();
        var limit = TakePositiveInt(arguments, "--limit");
        var epochs = TakePositiveInt(arguments, "--epochs");
        var maxSamples = TakePositiveInt(arguments, "--max-samples") ?? DefaultMaxSamples;
        var attempts = TakePositiveInt(arguments, "--attempts") ?? 1;
        var sandbox = TakeOption(arguments, "--sandbox")?.Trim().ToLowerInvariant();
        var logDir = TakeOption(arguments, "--log-dir") ?? DefaultLogDir;

        var sampleIds = new List<object>();
        for (string? id; (id = TakeOption(arguments, "--sample-id")) is not null;)
        {
            sampleIds.Add(int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : id);
        }

        int? maxTokens = null;
        var maxTokensSet = false;
        if (TakeOption(arguments, "--max-tokens") is { } maxTokensArg)
        {
            maxTokensSet = true;
            if (!maxTokensArg.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                maxTokens = int.TryParse(maxTokensArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                    ? value
                    : throw new UsageError($"--max-tokens expects a positive number or 'none', got '{maxTokensArg}'");
            }
        }

        string? reasoningEffort = null;
        if (TakeOption(arguments, "--reasoning-effort") is { } effortArg)
        {
            var effort = effortArg.Trim().ToLowerInvariant();
            if (!ReasoningParams.EffortLevels.Contains(effort, StringComparer.Ordinal))
            {
                throw new UsageError($"--reasoning-effort expects one of {string.Join("|", ReasoningParams.EffortLevels)}, got '{effortArg}'");
            }

            reasoningEffort = effort;
        }

        var modelArgs = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (string? pair; (pair = TakeOption(arguments, "--model-arg")) is not null;)
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new UsageError($"--model-arg expects key=value, got '{pair}'");
            }

            try
            {
                modelArgs[pair[..eq]] = ProviderUtil.ParseModelArgValue(pair[(eq + 1)..]);
            }
            catch (ArgumentException ex)
            {
                throw new UsageError($"--model-arg {pair[..eq]}: {ex.Message}");
            }
        }

        if (sandbox is not null && !SandboxTypes.Contains(sandbox, StringComparer.Ordinal))
        {
            throw new UsageError($"--sandbox expects {string.Join("|", SandboxTypes)}, got '{sandbox}'");
        }

        if (route is not null && !Routes.Contains(route, StringComparer.Ordinal))
        {
            throw new UsageError($"--route expects {string.Join("|", Routes)}, got '{route}'");
        }

        if (arguments.Find(argument => argument.StartsWith("--", StringComparison.Ordinal)) is { } stray)
        {
            throw new UsageError($"unknown or incomplete option '{stray}'");
        }

        return new RunOptions
        {
            Task = task,
            Agent = agent,
            Model = model,
            Route = route,
            Limit = limit,
            SampleIds = sampleIds,
            Epochs = epochs,
            MaxSamples = maxSamples,
            Attempts = attempts,
            Sandbox = sandbox,
            LogDir = logDir,
            Cleanup = !noCleanup,
            MaxTokens = maxTokens,
            MaxTokensSet = maxTokensSet,
            ReasoningEffort = reasoningEffort,
            ModelArgs = modelArgs,
            Fake = fake,
            Debug = debug,
        };
    }

    /// <summary>The Sample app's option parser: removes <c>name value</c> from the list and returns the value (null when absent or valueless).</summary>
    public static string? TakeOption(List<string> arguments, string name)
    {
        var index = arguments.IndexOf(name);
        if (index < 0 || index + 1 >= arguments.Count)
        {
            return null;
        }

        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }

    private static int? TakePositiveInt(List<string> arguments, string name)
    {
        var value = TakeOption(arguments, name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new UsageError($"{name} expects a positive number, got '{value}'");
    }
}
