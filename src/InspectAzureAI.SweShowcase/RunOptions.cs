using System.Globalization;
using System.Text.Json;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model.Cache;
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

    /// <summary>Port of <c>--log-format</c>: <c>eval</c> or <c>json</c>; null lets the runner pick (<c>INSPECT_LOG_FORMAT</c>, else <c>eval</c>).</summary>
    public LogFormat? LogFormat { get; init; }

    /// <summary>The <c>--approval</c> argument as given (a policy file or a registered approver name), for the header.</summary>
    public string? ApprovalSpec { get; init; }

    /// <summary>Port of <c>--approval</c>: the policies resolved from <see cref="ApprovalSpec"/> at parse time, so a bad file is a usage error.</summary>
    public ApprovalOption? Approval { get; init; }

    /// <summary>The <c>--cache</c> policy (Inspect's <c>generate(cache=...)</c>); null when caching is off.</summary>
    public CachePolicy? Cache { get; init; }

    /// <summary>The <c>--compaction</c> strategy, applied to the mini-swe and basic agent loops.</summary>
    public CompactionChoice? Compaction { get; init; }

    /// <summary>The <c>--hooks</c> entries (built-in hook names), created per run by <see cref="RunWiring.CreateHooks"/>.</summary>
    public IReadOnlyList<HookChoice> Hooks { get; init; } = [];

    /// <summary>Port of <c>--cost-limit</c>: dollars per sample; needs pricing for the model (see <see cref="ModelCostConfig"/>).</summary>
    public double? CostLimit { get; init; }

    /// <summary>Port of <c>--model-cost-config</c>: a JSON price file applied before the run.</summary>
    public string? ModelCostConfig { get; init; }

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

        LogFormat? logFormat = null;
        if (TakeOption(arguments, "--log-format") is { } logFormatArg)
        {
            try
            {
                logFormat = LogFormats.Parse(logFormatArg.Trim().ToLowerInvariant());
            }
            catch (ArgumentException)
            {
                throw new UsageError($"--log-format expects {string.Join("|", LogFormats.All.Select(f => f.Name()))}, got '{logFormatArg}'");
            }
        }

        var approvalSpec = TakeOption(arguments, "--approval");
        ApprovalOption? approval = null;
        if (approvalSpec is not null)
        {
            try
            {
                approval = ApprovalOption.FromPolicies(ApprovalPolicies.Resolve(approvalSpec));
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or NotSupportedException or JsonException or IOException)
            {
                throw new UsageError($"--approval: {ex.Message}");
            }
        }

        CachePolicy? cache = null;
        if (TakeOption(arguments, "--cache") is { } cacheArg)
        {
            var value = cacheArg.Trim();
            cache = value.ToLowerInvariant() switch
            {
                "off" or "false" or "none" => null,
                "on" or "true" => CachePolicy.Default,
                _ => CachePolicy.FromString(value) ?? throw new UsageError($"--cache expects on|off or an expiry such as 1W, 3D or 12h, got '{cacheArg}'"),
            };
        }

        var compaction = TakeOption(arguments, "--compaction") is { } compactionArg ? CompactionChoice.Parse(compactionArg) : null;

        var hooks = new List<HookChoice>();
        for (string? spec; (spec = TakeOption(arguments, "--hooks")) is not null;)
        {
            hooks.AddRange(spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(HookChoice.Parse));
        }

        double? costLimit = null;
        if (TakeOption(arguments, "--cost-limit") is { } costLimitArg)
        {
            costLimit = double.TryParse(costLimitArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var dollars) && dollars >= 0 && !double.IsNaN(dollars)
                ? dollars
                : throw new UsageError($"--cost-limit expects a non-negative number of dollars, got '{costLimitArg}'");
        }

        var modelCostConfig = TakeOption(arguments, "--model-cost-config");
        if (modelCostConfig is not null)
        {
            if (!File.Exists(modelCostConfig))
            {
                throw new UsageError($"--model-cost-config: {modelCostConfig} does not exist");
            }

            try
            {
                Eval.Model.Cost.ModelCostConfig.Parse(File.ReadAllText(modelCostConfig), modelCostConfig);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or IOException)
            {
                throw new UsageError($"--model-cost-config: {ex.Message}");
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
            LogFormat = logFormat,
            ApprovalSpec = approvalSpec,
            Approval = approval,
            Cache = cache,
            Compaction = compaction,
            Hooks = hooks,
            CostLimit = costLimit,
            ModelCostConfig = modelCostConfig,
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
