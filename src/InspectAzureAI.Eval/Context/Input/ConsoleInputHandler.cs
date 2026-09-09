namespace InspectAzureAI.Eval.Context.Input;

/// <summary>
/// Port of <c>util/_input/console.py</c> <c>console_handler</c>: walks the schema property by property with plain
/// prompts (through <see cref="InputScreen"/>, without its text-dump event since <see cref="InputHandlers.RequestInputAsync"/>
/// records the structured one). Returns accepted content, declined when the operator types <c>:decline</c>, or
/// cancelled on cancellation. Deviation: rich markup (dim/red/cyan) is dropped; end of input is also reported as
/// cancelled rather than raising, so a scripted console that runs dry ends the question cleanly.
/// </summary>
public sealed class ConsoleInputHandler(InputConsole? console = null) : IInputHandler
{
    public const string DeclineToken = ":decline";

    private static readonly object Omit = new();

    public async Task<InputResult> RequestAsync(InputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var screen = InputScreen.Open(header: null, recordEvent: false, console);
            return await AskSchemaAsync(request.Message, request.Schema, screen, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return InputResult.Cancelled();
        }
        catch (EndOfStreamException)
        {
            return InputResult.Cancelled();
        }
    }

    /// <summary>Port of <c>_ask_schema</c>: prints the title, message and description, then asks each property in order.</summary>
    public static async Task<InputResult> AskSchemaAsync(string message, ElicitationSchema schema, ConsoleInput console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(console);
        if (schema.Title is { } title)
        {
            console.Print(title);
        }

        console.Print(message);
        if (schema.Description is { } description)
        {
            console.Print(description);
        }

        console.Print($"(Type {DeclineToken} at any prompt to decline.)");

        var content = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in schema.Properties)
            {
                var value = await AskPropertyAsync(pair.Key, pair.Value, schema.IsRequired(pair.Key), console, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(value, Omit))
                {
                    content[pair.Key] = value;
                }
            }
        }
        catch (DeclinedException)
        {
            return InputResult.Declined();
        }

        return InputResult.Accepted(content);
    }

    private static Task<object?> AskPropertyAsync(string name, ElicitationProperty property, bool required, ConsoleInput console, CancellationToken cancellationToken)
    {
        var label = property.Title ?? name;
        if (property.Description is { } description)
        {
            console.Print(description);
        }

        return property switch
        {
            ElicitationStringProperty text => AskStringAsync(label, text, required, console, cancellationToken),
            ElicitationIntegerProperty integer => AskNumericAsync(label, integer, null, required, console, cancellationToken),
            ElicitationNumberProperty number => AskNumericAsync(label, null, number, required, console, cancellationToken),
            ElicitationBooleanProperty flag => AskBooleanAsync(label, flag, required, console, cancellationToken),
            ElicitationMultiSelectProperty multi => AskMultiSelectAsync(label, multi, required, console, cancellationToken),
            _ => throw new ArgumentException($"Unsupported property type: {property.GetType().Name}", nameof(property)),
        };
    }

    private static void CheckDecline(string value)
    {
        if (value.Trim() == DeclineToken)
        {
            throw new DeclinedException();
        }
    }

    private static async Task<object?> AskStringAsync(string label, ElicitationStringProperty property, bool required, ConsoleInput console, CancellationToken cancellationToken)
    {
        if (property.Format is { } format)
        {
            console.Print($"(format: {format})");
        }

        // print the options for bounded-choice strings (Python deliberately does not hand them to Prompt.ask so :decline still works)
        var labels = InputValidation.StringChoiceLabels(property);
        if (labels is not null)
        {
            if (property.OneOf is not null)
            {
                foreach (var (constant, title) in labels)
                {
                    console.Print($"  {constant}: {title}");
                }
            }
            else
            {
                console.Print($"options: {string.Join(", ", InputValidation.StringChoices(property) ?? [])}");
            }
        }

        while (true)
        {
            var value = await console.AskAsync(label, property.Default ?? "", showDefault: property.Default is not null, cancellationToken).ConfigureAwait(false);
            CheckDecline(value);
            if (value.Length == 0)
            {
                if (required)
                {
                    console.Print($"{label} is required.");
                    continue;
                }

                return Omit;
            }

            var (accepted, error) = InputValidation.ValidateString(property, value);
            if (error is not null)
            {
                console.Print(error);
                continue;
            }

            return accepted;
        }
    }

    private static async Task<object?> AskNumericAsync(string label, ElicitationIntegerProperty? integer, ElicitationNumberProperty? number, bool required, ConsoleInput console, CancellationToken cancellationToken)
    {
        // ask as a string so blank (optional → omit) and :decline can be detected, then validate per type
        string? minimum = integer?.Minimum?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? (number?.Minimum is { } min ? InputResult.PythonFloat(min) : null);
        string? maximum = integer?.Maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? (number?.Maximum is { } max ? InputResult.PythonFloat(max) : null);
        if (minimum is not null || maximum is not null)
        {
            var bounds = new List<string>();
            if (minimum is not null)
            {
                bounds.Add($">= {minimum}");
            }

            if (maximum is not null)
            {
                bounds.Add($"<= {maximum}");
            }

            console.Print($"({string.Join(", ", bounds)})");
        }

        var defaultText = integer?.Default?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? (number?.Default is { } d ? InputResult.PythonFloat(d) : null);
        while (true)
        {
            var raw = await console.AskAsync(label, defaultText ?? "", showDefault: defaultText is not null, cancellationToken).ConfigureAwait(false);
            CheckDecline(raw);
            if (raw.Length == 0)
            {
                if (required)
                {
                    console.Print($"{label} is required.");
                    continue;
                }

                return Omit;
            }

            object? result;
            string? error;
            if (integer is not null)
            {
                (var value, error) = InputValidation.ValidateInteger(integer, raw);
                result = value;
            }
            else
            {
                (var value, error) = InputValidation.ValidateNumber(number!, raw);
                result = value;
            }

            if (error is not null)
            {
                console.Print(error);
                continue;
            }

            return result;
        }
    }

    private static async Task<object?> AskBooleanAsync(string label, ElicitationBooleanProperty property, bool required, ConsoleInput console, CancellationToken cancellationToken)
    {
        // Prompt.ask rather than Confirm.ask so :decline and blank-means-omit work
        var defaultText = property.Default is { } flag ? (flag ? "y" : "n") : "";
        while (true)
        {
            var raw = await console.AskAsync($"{label} [y/n]", defaultText, showDefault: defaultText.Length > 0, cancellationToken).ConfigureAwait(false);
            CheckDecline(raw);
            var value = raw.Trim().ToLowerInvariant();
            if (value.Length == 0)
            {
                if (required)
                {
                    console.Print($"{label} is required.");
                    continue;
                }

                return Omit;
            }

            switch (value)
            {
                case "y" or "yes" or "true":
                    return true;
                case "n" or "no" or "false":
                    return false;
                default:
                    console.Print("Please answer y or n.");
                    break;
            }
        }
    }

    private static async Task<object?> AskMultiSelectAsync(string label, ElicitationMultiSelectProperty property, bool required, ConsoleInput console, CancellationToken cancellationToken)
    {
        var options = InputValidation.MultiSelectOptions(property);
        for (var i = 0; i < options.Count; i++)
        {
            var (constant, title) = options[i];
            console.Print(constant == title ? $"  {i + 1}: {title}" : $"  {i + 1}: {title} ({constant})");
        }

        if (property.MinItems is not null || property.MaxItems is not null)
        {
            var bounds = new List<string>();
            if (property.MinItems is { } min)
            {
                bounds.Add($"min {min}");
            }

            if (property.MaxItems is { } max)
            {
                bounds.Add($"max {max}");
            }

            console.Print($"({string.Join(", ", bounds)})");
        }

        var defaultText = "";
        if (property.Default is { Count: > 0 } defaults)
        {
            // map the default values back to indices for display
            var indexByConst = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < options.Count; i++)
            {
                indexByConst.TryAdd(options[i].Const, i + 1);
            }

            defaultText = string.Join(",", defaults.Where(indexByConst.ContainsKey).Select(value => indexByConst[value].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        while (true)
        {
            var raw = await console.AskAsync($"{label} (comma-separated indices)", defaultText, showDefault: defaultText.Length > 0, cancellationToken).ConfigureAwait(false);
            CheckDecline(raw);
            if (raw.Length == 0)
            {
                // an empty selection is schema-valid when min_items is unset or 0, even for a required array
                var minRequired = property.MinItems ?? 0;
                if (minRequired == 0)
                {
                    return required ? new List<string>() : Omit;
                }

                console.Print($"Select at least {minRequired}.");
                continue;
            }

            var parts = raw.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
            var indices = new List<int>();
            var parsed = true;
            foreach (var part in parts)
            {
                if (int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
                {
                    indices.Add(index);
                }
                else
                {
                    parsed = false;
                    break;
                }
            }

            if (!parsed)
            {
                console.Print("Enter comma-separated index numbers (e.g. 1,3).");
                continue;
            }

            if (indices.Any(i => i < 1 || i > options.Count))
            {
                console.Print($"Indices must be between 1 and {options.Count}.");
                continue;
            }

            var values = indices.Distinct().Select(i => options[i - 1].Const).ToList();
            var (accepted, error) = InputValidation.ValidateMultiSelect(property, values);
            if (error is not null)
            {
                console.Print(error);
                continue;
            }

            return accepted!.ToList();
        }
    }

    /// <summary>The user typed <c>:decline</c> at a prompt.</summary>
    private sealed class DeclinedException : Exception;
}
