using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

public static partial class BuiltinTools
{
    /// <summary>The <c>ask_user</c> tool description (the Python <c>execute</c> docstring up to its Args section, verbatim).</summary>
    public const string AskUserDescription =
        """
        Ask the human operator a structured question and return their answer.

        The operator is shown the question and answers via a console (or UI)
        prompt; their structured response is returned to you as a JSON object.

        ## When to use
        - Information you cannot derive from task context or tools
          (credentials, preferences, missing parameters)
        - The operator should choose between several reasonable branches
          before you commit to one
        - Confirmation before something hard to reverse

        ## When NOT to use
        - You can look it up yourself (read a file, query a tool)
        - You're merely uncertain — pick the most reasonable option
        - You need free-form chat (this is a one-shot structured form)

        ## Schema shape

        Top-level: `{"type": "object", "properties": {...}, "required": [...]}`.
        Each property declares one form field. Use the smallest set that
        answers your question — long forms get abandoned. The conversational
        prompt lives on `message` (above); per-field guidance lives in each
        property's `title` and `description`.

        Property types: `"string"`, `"integer"`, `"number"`, `"boolean"`, `"array"`.

        ## Examples

        Simple required string:
          {"type": "object",
           "properties": {"name": {"type": "string", "description": "Your name"}},
           "required": ["name"]}

        Enum choice (string with bounded options):
          {"type": "object",
           "properties": {"color": {"type": "string", "enum": ["red", "green", "blue"]}},
           "required": ["color"]}

        Confirmation (boolean):
          {"type": "object",
           "properties": {"proceed": {"type": "boolean",
                                      "description": "Continue with the operation?"}},
           "required": ["proceed"]}

        Multi-property form with bounded integer:
          {"type": "object",
           "properties": {
             "url": {"type": "string", "description": "API endpoint"},
             "timeout": {"type": "integer", "minimum": 1, "maximum": 300}
           },
           "required": ["url"]}

        Multi-select array (operator picks 1+ items):
          {"type": "object",
           "properties": {"status": {
             "type": "array",
             "items": {"any_of": [
               {"const": "draft", "title": "Draft"},
               {"const": "pub",   "title": "Published"}
             ]},
             "min_items": 1
           }},
           "required": ["status"]}

        ## Constraints per property type
        - string: `enum`, `min_length`, `max_length`, `pattern`, `format`
        - integer / number: `minimum`, `maximum`
        - boolean: no extra constraints
        - array (multi-select): `min_items`, `max_items`; `items.any_of` for titled
          choices (as above) or `items.enum` for bare string choices
        """;

    public const string AskUserDeclined = "User declined to answer the question.";

    public const string AskUserCancelled = "Question was cancelled before the user answered.";

    /// <summary>
    /// Port of <c>ask_user()</c> (<c>tool/_tools/_ask_user.py</c>): asks the human operator a structured question
    /// (<c>message</c> plus a JSON-Schema-shaped <c>schema</c>) through <paramref name="handler"/> (or
    /// <see cref="InputHandlers.Default"/>, resolved per call) and returns the answer as a JSON object string. A
    /// schema the handlers cannot render is a <see cref="ToolError"/> starting <c>Invalid schema:</c>; a declined or
    /// cancelled question is a <see cref="ToolError"/> with Python's message. Deviation: the pydantic validation
    /// details in the error are replaced by <see cref="ElicitationSchema.Parse"/>'s path-qualified message.
    /// </summary>
    public static ToolDef AskUser(IInputHandler? handler = null)
    {
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["message"] = ToolParam.Of("string", "The prompt to show the operator."),
                ["schema"] = new ToolParam
                {
                    Type = ["object"],
                    Description = "JSON-Schema-shaped dict describing the answer fields.",
                    AdditionalProperties = new ToolParam(),
                },
            },
            Required = ["message", "schema"],
        };
        return new ToolDef("ask_user", AskUserDescription, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            var message = ToolArguments.String(arguments, "message");
            ElicitationSchema schema;
            try
            {
                schema = ElicitationSchema.Parse(arguments["schema"]);
            }
            catch (ElicitationSchemaException ex)
            {
                throw new ToolError($"Invalid schema: {ex.Message}");
            }

            var result = await InputHandlers.RequestInputAsync(message, schema, handler, cancellationToken).ConfigureAwait(false);
            return result.Outcome switch
            {
                InputOutcome.Accepted => (ToolResult)result.ContentJson(),
                InputOutcome.Declined => throw new ToolError(AskUserDeclined),
                _ => throw new ToolError(AskUserCancelled),
            };
        });
    }
}
