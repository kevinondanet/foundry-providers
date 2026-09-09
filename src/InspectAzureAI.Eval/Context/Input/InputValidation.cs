using System.Globalization;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Context.Input;

/// <summary>
/// Port of <c>util/_input/_validate.py</c>: the per-property validation predicates shared by the console handler
/// (and any host-supplied handler). Each returns the accepted value and a null error, or a null value and Python's
/// human-readable error message.
/// </summary>
public static class InputValidation
{
    /// <summary>Port of <c>validate_string</c>.</summary>
    public static (string? Value, string? Error) ValidateString(ElicitationStringProperty property, string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);
        var choices = StringChoices(property);
        if (choices is not null && !choices.Contains(value, StringComparer.Ordinal))
        {
            return (null, $"Please choose one of: {string.Join(", ", choices)}.");
        }

        if (property.MinLength is { } min && value.Length < min)
        {
            return (null, $"Must be at least {min} characters.");
        }

        if (property.MaxLength is { } max && value.Length > max)
        {
            return (null, $"Must be at most {max} characters.");
        }

        if (property.Pattern is { } pattern && !FullMatch(pattern, value))
        {
            return (null, $"Must match pattern: {pattern}");
        }

        return (value, null);
    }

    /// <summary>Port of <c>validate_integer</c>: parses <paramref name="raw"/> as Python's <c>int()</c> would and checks the bounds.</summary>
    public static (long? Value, string? Error) ValidateInteger(ElicitationIntegerProperty property, string raw)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(raw);
        if (!long.TryParse(raw.Replace("_", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return (null, "Please enter a valid integer.");
        }

        if (property.Minimum is { } min && value < min)
        {
            return (null, $"Must be >= {min}.");
        }

        if (property.Maximum is { } max && value > max)
        {
            return (null, $"Must be <= {max}.");
        }

        return (value, null);
    }

    /// <summary>Port of <c>validate_number</c>: parses <paramref name="raw"/> as Python's <c>float()</c> would and checks the bounds.</summary>
    public static (double? Value, string? Error) ValidateNumber(ElicitationNumberProperty property, string raw)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(raw);
        if (!TryParseFloat(raw, out var value))
        {
            return (null, "Please enter a valid number.");
        }

        if (property.Minimum is { } min && value < min)
        {
            return (null, $"Must be >= {InputResult.PythonFloat(min)}.");
        }

        if (property.Maximum is { } max && value > max)
        {
            return (null, $"Must be <= {InputResult.PythonFloat(max)}.");
        }

        return (value, null);
    }

    /// <summary>Port of <c>validate_multiselect</c>: every value must be an allowed const; duplicates are dropped (first occurrence kept) before the item bounds apply.</summary>
    public static (IReadOnlyList<string>? Values, string? Error) ValidateMultiSelect(ElicitationMultiSelectProperty property, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);
        var allowed = MultiSelectOptions(property).Select(option => option.Const).ToHashSet(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!allowed.Contains(value))
            {
                return (null, $"'{value}' is not a valid choice.");
            }
        }

        var unique = values.Distinct(StringComparer.Ordinal).ToList();
        if (property.MinItems is { } min && unique.Count < min)
        {
            return (null, $"Select at least {min}.");
        }

        if (property.MaxItems is { } max && unique.Count > max)
        {
            return (null, $"Select at most {max}.");
        }

        return (unique, null);
    }

    /// <summary>Port of <c>multiselect_options</c>: the <c>(const, label)</c> pairs of a multi-select property.</summary>
    public static IReadOnlyList<(string Const, string Label)> MultiSelectOptions(ElicitationMultiSelectProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Items switch
        {
            TitledMultiSelectItems titled => titled.AnyOf.Select(option => (option.Const, option.Title)).ToList(),
            StringMultiSelectItems strings => strings.Enum.Select(value => (value, value)).ToList(),
            _ => throw new ArgumentException($"Unsupported multi-select items: {property.Items.GetType().Name}", nameof(property)),
        };
    }

    /// <summary>Port of <c>string_choices</c>: the closed set of allowed strings, or null when free-form.</summary>
    public static IReadOnlyList<string>? StringChoices(ElicitationStringProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.OneOf is not null)
        {
            return property.OneOf.Select(option => option.Const).ToList();
        }

        return property.Enum?.ToList();
    }

    /// <summary>Port of <c>string_choice_labels</c>: <c>(const, label)</c> pairs for a bounded string (the label is the option title for <c>one_of</c>, the value itself for <c>enum</c>), or null when free-form.</summary>
    public static IReadOnlyList<(string Const, string Label)>? StringChoiceLabels(ElicitationStringProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.OneOf is not null)
        {
            return property.OneOf.Select(option => (option.Const, option.Title)).ToList();
        }

        return property.Enum?.Select(value => (value, value)).ToList();
    }

    private static bool FullMatch(string pattern, string value)
    {
        var match = Regex.Match(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        while (match.Success)
        {
            if (match.Index == 0 && match.Length == value.Length)
            {
                return true;
            }

            match = match.NextMatch();
        }

        // re.fullmatch anchors both ends; a shorter leftmost match does not preclude a full one
        return Regex.IsMatch(value, $"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static bool TryParseFloat(string raw, out double value)
    {
        var text = raw.Trim().Replace("_", "", StringComparison.Ordinal);
        switch (text.ToLowerInvariant())
        {
            case "inf" or "+inf" or "infinity" or "+infinity":
                value = double.PositiveInfinity;
                return true;
            case "-inf" or "-infinity":
                value = double.NegativeInfinity;
                return true;
            case "nan" or "+nan" or "-nan":
                value = double.NaN;
                return true;
            default:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
