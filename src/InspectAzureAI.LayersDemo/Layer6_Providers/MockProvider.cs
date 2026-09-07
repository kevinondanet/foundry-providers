// ============================================================================
//  LAYER 6: PROVIDERS
//  Python: inspect_ai/model/_providers  (openai.py, anthropic.py, google.py, ...)
//
//  A provider is the vendor adapter: it converts Inspect's neutral ChatMessage
//  list into the vendor's wire format, sends it, and converts the reply back
//  into a ModelOutput. It also tells the model layer which of its own
//  exceptions are worth retrying.
//
//  Providers live in an underscore package. Nobody imports them: get_model()
//  resolves the provider name through the registry, so a new provider is a
//  new file plus one `@modelapi` decorator. Their only upward reference is to
//  the abstract ModelAPI contract in layer 5 (dependency inversion — the
//  contract is owned by the caller, the implementation by the callee).
//
//  The demo provider is scripted so the app runs offline: it asks for the
//  calculator when it sees arithmetic, asks for bash when it sees a file
//  name, and answers from the tool result otherwise. Its first call ever
//  fails with a rate-limit error so you can watch layer 5 retry.
// ============================================================================
using System.Text.Json;
using System.Text.RegularExpressions;
using inspect_ai._util.display;
using inspect_ai.model;

namespace inspect_ai.model._providers;

/// <summary>A provider-specific exception; layer 5 never sees the type, only ShouldRetry's answer.</summary>
internal sealed class RateLimitException(string message) : Exception(message);

internal static class MockProviderRegistration
{
    /// <summary>Python: `@modelapi(name="mock")` on a function returning the class.</summary>
    [ModelApi("mock")]
    public static ModelAPI Create(string modelName) => new MockModelAPI(modelName);
}

internal sealed partial class MockModelAPI(string modelName) : ModelAPI(modelName)
{
    private static bool _simulatedOutageUsed;   // module-level state, no lock: single event loop
    private int _callCounter;

    public override bool ShouldRetry(Exception ex) => ex is RateLimitException;

    public override async Task<ModelOutput> Generate(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, GenerateConfig config)
    {
        // 1. Inspect messages -> vendor wire format (here: an OpenAI-shaped body).
        var requestBody = new
        {
            model = ModelName,
            temperature = config.Temperature,
            max_tokens = config.MaxTokens,
            messages = input.Select(m => new { role = m.Role, content = m.Content, tool_call_id = m.ToolCallId }),
            tools = tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } }),
        };
        var request = JsonSerializer.Serialize(requestBody);
        Display.Step("L6 _providers/mock", $"POST /v1/chat/completions ({request.Length} bytes)");

        // 2. "Send" it. The first request in the process hits a fake 429.
        await Task.Delay(20);
        if (!_simulatedOutageUsed)
        {
            _simulatedOutageUsed = true;
            Display.Step("L6 _providers/mock", "HTTP 429 Too Many Requests");
            throw new RateLimitException("429 Too Many Requests (simulated)");
        }

        // 3. Decide what the "model" says, in vendor format.
        var (content, toolCall, finishReason) = Script(input, tools);
        var responseBody = new
        {
            id = $"chatcmpl-{++_callCounter}",
            choices = new[]
            {
                new
                {
                    finish_reason = finishReason,
                    message = new
                    {
                        role = "assistant",
                        content,
                        tool_calls = toolCall is null ? null : new[]
                        {
                            new { id = toolCall.Id, type = "function", function = new { name = toolCall.Function, arguments = JsonSerializer.Serialize(toolCall.Arguments) } },
                        },
                    },
                },
            },
            usage = new { prompt_tokens = request.Length / 4, completion_tokens = content.Length / 4 },
        };
        var response = JsonSerializer.Serialize(responseBody);
        Display.Step("L6 _providers/mock", $"HTTP 200, finish_reason={finishReason}" + (toolCall is null ? "" : $", tool_call={toolCall.Function}"));

        // 4. Vendor format -> Inspect's ModelOutput.
        var message = new ChatMessage("assistant", content, toolCall is null ? null : new[] { toolCall });
        return new ModelOutput(message, finishReason, new ModelUsage(request.Length / 4, content.Length / 4), new ModelCall(request, response));
    }

    /// <summary>The script that stands in for a real model.</summary>
    private static (string Content, ToolCall? Call, string FinishReason) Script(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools)
    {
        var last = input[^1];
        var question = input.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        // A tool just answered: turn its result into a final answer.
        if (last.Role == "tool")
            return last.Function switch
            {
                "calculator" => ($"The answer is {last.Content.Trim()}.", null, "stop"),
                "bash" => ($"The file says: {last.Content.Trim()}", null, "stop"),
                _ => ($"Tool result: {last.Content.Trim()}", null, "stop"),
            };

        // Arithmetic in the question and a calculator on offer -> call it.
        var arithmetic = ArithmeticPattern().Match(question);
        if (arithmetic.Success && tools.Any(t => t.Name == "calculator"))
            return ("", new ToolCall("call_1", "calculator", new Dictionary<string, string> { ["expression"] = arithmetic.Value }), "tool_calls");

        // A file name in the question and bash on offer -> read it.
        var file = FilePattern().Match(question);
        if (file.Success && tools.Any(t => t.Name == "bash"))
            return ("", new ToolCall("call_2", "bash", new Dictionary<string, string> { ["cmd"] = $"cat {file.Value}" }), "tool_calls");

        return ("I do not know.", null, "stop");
    }

    [GeneratedRegex(@"\d+\s*[-+*/]\s*\d+")] private static partial Regex ArithmeticPattern();
    [GeneratedRegex(@"[\w.-]+\.txt")] private static partial Regex FilePattern();
}
