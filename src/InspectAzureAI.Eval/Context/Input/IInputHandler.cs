using System.Text;

namespace InspectAzureAI.Eval.Context.Input;

/// <summary>
/// The surface that collects the operator's answer to an <see cref="InputRequest"/>. Python dispatches to an ACP
/// client, the Textual panel or the console (<c>util/_input/builtin.py</c> <c>_dispatch_builtin</c>); this port
/// ships the console (<see cref="ConsoleInputHandler"/>) and lets a host plug in its own through
/// <see cref="InputHandlers.Default"/>. Deviation: the ACP and Textual panel handlers are not ported.
/// </summary>
public interface IInputHandler
{
    /// <summary>Presents <paramref name="request"/> and returns the outcome (with the content when accepted).</summary>
    Task<InputResult> RequestAsync(InputRequest request, CancellationToken cancellationToken);
}

/// <summary>Port of <c>util/_input/request.py</c>: <see cref="RequestInputAsync"/> is <c>request_input</c>.</summary>
public static class InputHandlers
{
    private static IInputHandler _default = new ConsoleInputHandler();

    /// <summary>The handler <see cref="RequestInputAsync"/> (and the <c>ask_user</c> tool) uses when none is given; the console by default.</summary>
    public static IInputHandler Default
    {
        get => _default;
        set => _default = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Port of <c>request_input</c>: asks the user a structured question through <paramref name="handler"/> (or
    /// <see cref="Default"/>, resolved per call), then records an <see cref="InputEvent"/> carrying the message, the
    /// requested fields, the outcome and the content in the current sample's transcript (when one is active).
    /// Deviation: Python's Apprise notification and the <c>awaiting_human("question")</c> marker have no port.
    /// </summary>
    public static async Task<InputResult> RequestInputAsync(string message, ElicitationSchema schema, IInputHandler? handler = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(schema);
        var request = new InputRequest(message, schema);
        var result = await (handler ?? Default).RequestAsync(request, cancellationToken).ConfigureAwait(false);
        SampleContext.Current?.Transcript.Add(CreateInputEvent(request, result));
        return result;
    }

    /// <summary>Port of <c>_record_input_event</c>: the event for a <paramref name="request"/> / <paramref name="result"/> pair (fields from the schema, text synthesized from the answer).</summary>
    public static InputEvent CreateInputEvent(InputRequest request, InputResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var fields = FieldsFromSchema(request.Schema);
        var text = SynthesizeText(request.Message, fields, result);
        return new InputEvent(text, text)
        {
            Message = request.Message,
            Fields = fields,
            Outcome = result.Outcome.ToPython(),
            Content = result.Content,
        };
    }

    /// <summary>Port of <c>_fields_from_schema</c>.</summary>
    internal static IReadOnlyList<InputField> FieldsFromSchema(ElicitationSchema schema) =>
        schema.Properties.Select(pair => new InputField(pair.Key, pair.Value.Type) { Description = pair.Value.Description }).ToList();

    /// <summary>Port of <c>_synthesize_text</c>: the message, then <c>  name: value</c> per answered field, or <c>[declined]</c> / <c>[cancelled]</c>.</summary>
    internal static string SynthesizeText(string message, IReadOnlyList<InputField> fields, InputResult result)
    {
        var lines = new StringBuilder(message);
        if (result.Outcome == InputOutcome.Accepted && result.Content is { Count: > 0 } content)
        {
            foreach (var field in fields)
            {
                if (content.TryGetValue(field.Name, out var value))
                {
                    lines.Append('\n').Append("  ").Append(field.Name).Append(": ").Append(InputResult.PythonText(value));
                }
            }
        }
        else if (result.Outcome == InputOutcome.Declined)
        {
            lines.Append("\n[declined]");
        }
        else if (result.Outcome == InputOutcome.Cancelled)
        {
            lines.Append("\n[cancelled]");
        }

        return lines.ToString();
    }
}
