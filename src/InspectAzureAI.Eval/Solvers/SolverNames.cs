using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Solvers;

public static partial class Solvers
{
    [GeneratedRegex("^<([^>]+)>(?:g__([^|]+)\\|)?")]
    private static partial Regex CompilerGeneratedName();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundary();

    /// <summary>Whether <paramref name="solver"/> is a <see cref="Chain"/> (Python's <c>isinstance(solver, Chain)</c>).</summary>
    public static bool IsChain(Solver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        return solver.Target is ChainSolver;
    }

    /// <summary>
    /// Stand-in for Python's <c>registry_log_name(solver)</c>, the span name of a chain step: <c>"chain"</c> for a
    /// chain, otherwise the delegate's method name in snake_case — a lambda returned by a factory such as
    /// <c>UseTools</c> is named after the factory (<c>"use_tools"</c>), a local function after itself.
    /// </summary>
    public static string LogName(Solver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        if (IsChain(solver))
        {
            return "chain";
        }

        var name = solver.Method.Name;
        if (CompilerGeneratedName().Match(name) is { Success: true } match)
        {
            name = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value;
        }

        return SnakeCase(name);
    }

    private static string SnakeCase(string name) => WordBoundary().Replace(name, "_").ToLowerInvariant();
}
