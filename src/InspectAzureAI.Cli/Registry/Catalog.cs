using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Registry;

/// <summary>
/// The CLI's stand-in for Python's registry lookups of solvers, scorers, metrics and reducers by name
/// (<c>--solver</c>, <c>--scorer</c>, <c>--metric</c>, <c>--epochs-reducer</c>): the built-in factories on
/// <see cref="Solvers"/>, <see cref="Scorers"/>, <see cref="Metrics"/> and <see cref="Reducers"/> are found by their
/// Python names (<c>model_graded_qa</c> → <c>ModelGradedQa</c>) and the <c>-S</c> arguments are bound to their
/// parameters (see <see cref="ParameterBinder"/>). Unknown names are a <see cref="PrerequisiteError"/> listing what exists.
/// </summary>
public static partial class Catalog
{
    private static readonly IReadOnlyDictionary<string, string> ScorerAliases = new Dictionary<string, string>(StringComparer.Ordinal) { ["exact"] = "ExactMatch" };

    /// <summary>Creates a built-in scorer by its Python name with the given arguments.</summary>
    public static ScorerDef CreateScorer(string name, IReadOnlyDictionary<string, object?>? args = null) =>
        Invoke<ScorerDef>(typeof(Scorers), name, args, "scorer", ScorerAliases, excluded: ["Custom"]);

    /// <summary>Creates a built-in solver by its Python name with the given arguments.</summary>
    public static Solver CreateSolver(string name, IReadOnlyDictionary<string, object?>? args = null) =>
        Invoke<Solver>(typeof(Solvers), name, args, "solver", null, excluded: ["Chain", "UseTools"]);

    /// <summary>Creates a built-in metric by its Python name.</summary>
    public static MetricDef CreateMetric(string name) =>
        Invoke<MetricDef>(typeof(Metrics), name, null, "metric", null, excluded: []);

    /// <summary>
    /// Port of <c>create_reducers</c>: <c>mean</c>, <c>median</c>, <c>mode</c>, <c>max</c>, and the <c>name_k</c> shorthand
    /// (<c>at_least_2</c>, <c>pass_at_5</c>) when the literal name is not itself a reducer.
    /// </summary>
    public static ScoreReducer CreateReducer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Find(typeof(Reducers), typeof(ScoreReducer), name, null, ["NameOf"]) is { } direct)
        {
            return InvokeFactory<ScoreReducer>(direct, null, $"reducer '{name}'");
        }

        var match = ReducerShorthand().Match(name);
        if (match.Success && Find(typeof(Reducers), typeof(ScoreReducer), match.Groups[1].Value, null, ["NameOf"]) is { } parameterized)
        {
            var args = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) };
            return InvokeFactory<ScoreReducer>(parameterized, args, $"reducer '{name}'");
        }

        throw new PrerequisiteError($"Reducer '{name}' not found. Built-in reducers: {Names(typeof(Reducers), typeof(ScoreReducer), ["NameOf"])} (add _k for at_least and pass_at, e.g. at_least_2).");
    }

    /// <summary>Port of <c>create_reducers</c> over a list of names; null for null.</summary>
    public static IReadOnlyList<ScoreReducer>? CreateReducers(IEnumerable<string>? names) =>
        names?.Select(CreateReducer).ToList();

    /// <summary>
    /// The scorers a log was scored with (<c>eval.scorers</c>), re-created by name and recorded options — the port of
    /// <c>resolve_scorers</c> without a registry. Empty when the header lists none.
    /// </summary>
    public static IReadOnlyList<ScorerDef> ScorersFromLog(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return (log.Eval.Scorers ?? []).Select(scorer => CreateScorer(scorer.Name, scorer.Options)).ToList();
    }

    /// <summary>The Python names of the built-in factories of a kind (for help and error messages).</summary>
    public static IReadOnlyList<string> BuiltinNames(string kind) => kind switch
    {
        "scorer" => Names(typeof(Scorers), typeof(ScorerDef), ["Custom"]).Split(", "),
        "solver" => Names(typeof(Solvers), typeof(Solver), ["Chain", "UseTools"]).Split(", "),
        "metric" => Names(typeof(Metrics), typeof(MetricDef), []).Split(", "),
        "reducer" => Names(typeof(Reducers), typeof(ScoreReducer), ["NameOf"]).Split(", "),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "expected scorer, solver, metric or reducer"),
    };

    private static T Invoke<T>(Type owner, string name, IReadOnlyDictionary<string, object?>? args, string kind, IReadOnlyDictionary<string, string>? aliases, string[] excluded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var method = Find(owner, typeof(T), name, aliases, excluded)
            ?? throw new PrerequisiteError($"{Capitalize(kind)} '{name}' not found. Built-in {kind}s: {Names(owner, typeof(T), excluded)}.");
        return InvokeFactory<T>(method, args, $"{kind} '{name}'");
    }

    private static T InvokeFactory<T>(MethodInfo method, IReadOnlyDictionary<string, object?>? args, string subject)
    {
        var values = ParameterBinder.Bind(method, args ?? new Dictionary<string, object?>(StringComparer.Ordinal), subject);
        try
        {
            return (T)method.Invoke(null, values)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static MethodInfo? Find(Type owner, Type returnType, string name, IReadOnlyDictionary<string, string>? aliases, string[] excluded)
    {
        var wanted = aliases is not null && aliases.TryGetValue(name, out var alias) ? ParameterBinder.Normalize(alias) : ParameterBinder.Normalize(name);
        return Factories(owner, returnType, excluded).FirstOrDefault(method => ParameterBinder.Normalize(method.Name) == wanted);
    }

    private static IEnumerable<MethodInfo> Factories(Type owner, Type returnType, string[] excluded) =>
        owner.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == returnType && !excluded.Contains(method.Name, StringComparer.Ordinal) && !method.IsGenericMethodDefinition)
            .Where(method => method.GetParameters().All(parameter => !typeof(Delegate).IsAssignableFrom(parameter.ParameterType) || parameter.HasDefaultValue))
            .OrderBy(method => method.Name, StringComparer.Ordinal);

    private static string Names(Type owner, Type returnType, string[] excluded) =>
        string.Join(", ", Factories(owner, returnType, excluded).Select(method => ParameterBinder.SnakeCase(method.Name)).Distinct());

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"^(.*?)_(\d+)$")]
    private static partial Regex ReducerShorthand();
}
