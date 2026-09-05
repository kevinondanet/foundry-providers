using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

namespace InspectAzureAI.Cli.Commands;

/// <summary>A bare-flag-or-value option's state: not given, given bare (<see cref="Value"/> null) or given with a value.</summary>
internal readonly record struct FlagValue(bool Given, string? Value)
{
    public static FlagValue NotGiven => new(false, null);

    public bool IsBare => Given && Value is null;
}

/// <summary>
/// Option factories that reproduce click's conventions: an <c>envvar</c> supplies the default (a <c>multiple</c>
/// option's variable is split on whitespace), <c>click.Choice</c> validation, and the <c>is_flag=False, flag_value=...</c>
/// shape of an option that may be given bare or with a value.
/// </summary>
internal static class Opt
{
    public static Option<string?> String(string name, string description, string? env = null, params string[] aliases)
    {
        var option = new Option<string?>(name, aliases) { Description = Help(description, env) };
        if (env is not null)
        {
            option.DefaultValueFactory = _ => EnvText(env);
        }

        return option;
    }

    public static Option<int?> Int(string name, string description, string? env = null)
    {
        var option = new Option<int?>(name) { Description = Help(description, env) };
        if (env is not null)
        {
            option.DefaultValueFactory = result => EnvParse(result, env, text => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), "an integer");
        }

        return option;
    }

    public static Option<double?> Double(string name, string description, string? env = null)
    {
        var option = new Option<double?>(name) { Description = Help(description, env) };
        if (env is not null)
        {
            option.DefaultValueFactory = result => EnvParse(result, env, text => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), "a number");
        }

        return option;
    }

    public static Option<bool> Flag(string name, string description, string? env = null, bool hidden = false)
    {
        var option = new Option<bool>(name) { Description = Help(description, env), Hidden = hidden };
        if (env is not null)
        {
            option.DefaultValueFactory = _ => EnvFlag(env);
        }

        return option;
    }

    public static Option<string[]> Multi(string name, string description, string? env = null, params string[] aliases)
    {
        var option = new Option<string[]>(name, aliases) { Description = Help(description, env), AllowMultipleArgumentsPerToken = false };
        if (env is not null)
        {
            option.DefaultValueFactory = _ => EnvText(env)?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        }

        return option;
    }

    public static Option<string?> Choice(string name, string description, string[] choices, string? env = null, bool caseInsensitive = false)
    {
        var option = String(name, $"{description} [{string.Join("|", choices)}]", env);
        var comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (value is not null && !choices.Contains(value, comparer))
            {
                result.AddError($"Invalid value for '{name}': '{value}' is not one of {string.Join(", ", choices.Select(choice => $"'{choice}'"))}.");
            }
        });
        return option;
    }

    /// <summary>An option that can be given bare (<c>--cache</c>) or with a value (<c>--cache 3</c>); read it with <see cref="FlagOrValueOf"/>.</summary>
    public static Option<string?> FlagOrValue(string name, string description, string? env = null)
    {
        var option = String(name, description, env);
        option.Arity = ArgumentArity.ZeroOrOne;
        return option;
    }

    /// <summary>Rejects option-looking values (<c>--bogus</c>) that a positional argument would otherwise swallow, so a mistyped option is a usage error as in click.</summary>
    public static T RejectOptionLike<T>(this T argument)
        where T : Argument
    {
        argument.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (token.Value.Length > 1 && token.Value.StartsWith('-'))
                {
                    result.AddError($"Unrecognized option '{token.Value}'.");
                    return;
                }
            }
        });
        return argument;
    }

    /// <summary>Whether the option was typed on the command line (not defaulted, not from its environment variable).</summary>
    public static bool Specified(ParseResult result, Option option) => result.GetResult(option) is OptionResult { Implicit: false };

    public static FlagValue FlagOrValueOf(ParseResult result, Option<string?> option)
    {
        var symbol = result.GetResult(option);
        if (symbol is null)
        {
            return FlagValue.NotGiven;
        }

        var value = result.GetValue(option);
        if (!symbol.Implicit)
        {
            // typed bare: no value token, whatever the environment default would supply
            return new FlagValue(true, symbol.Tokens.Count == 0 ? null : value);
        }

        return value is null ? FlagValue.NotGiven : new FlagValue(true, value);
    }

    /// <summary>The value of the first environment variable that is set, or null.</summary>
    public static string? EnvText(params string[] names)
    {
        foreach (var name in names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static bool EnvFlag(string env) => EnvText(env) is { } value && value.ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static T? EnvParse<T>(ArgumentResult result, string env, Func<string, T> parse, string expected)
        where T : struct
    {
        var text = EnvText(env);
        if (text is null)
        {
            return null;
        }

        try
        {
            return parse(text);
        }
        catch (FormatException)
        {
            result.AddError($"{env}: expected {expected}, got '{text}'.");
            return null;
        }
    }

    private static string Help(string description, string? env) => env is null ? description : $"{description} [env: {env}]";
}
