// ============================================================================
//  LAYER 4: THE TASK-AUTHORING API (part 3: tools)
//  Python: inspect_ai/tool
//
//  A tool is a named async function with a parameter schema. use_tools() puts
//  it on the TaskState; the model layer sends only its ToolInfo (name,
//  description, schema) to the provider; when the model replies with a
//  ToolCall, execute_tools() runs the matching function and turns the result
//  into a "tool" message for the next generate().
//
//  Tools are where layer 4 reaches DOWN to layer 7: `bash()` runs its command
//  through `sandbox()`, which talks to inspect_sandbox_tools in the container.
//  (Python keeps execute_tools in model/_call_tools.py; the demo keeps it
//  beside the Tool type for readability — the direction of calls is the same.)
// ============================================================================
using System.Text.Json;
using inspect_ai._util.display;
using inspect_ai._util.registry;
using inspect_ai.log;
using inspect_ai.model;
using inspect_ai.util;

namespace inspect_ai.tool;

/// <summary>A callable tool (Python: Tool + ToolDef).</summary>
public sealed record Tool(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> Parameters,
    Func<IReadOnlyDictionary<string, string>, Task<string>> Execute)
{
    /// <summary>The model-facing view: schema only, no code.</summary>
    public ToolInfo Info => new(Name, Description, Parameters);
}

/// <summary>Python's `@tool` decorator.</summary>
public sealed class ToolAttribute(string name) : RegistryAttribute(RegistryType.Tool, name);

public static class Tools
{
    /// <summary>A pure, in-process tool: evaluates "a op b".</summary>
    [Tool("calculator")]
    public static Tool calculator() => new(
        "calculator",
        "Evaluate a simple arithmetic expression of the form 'a op b'.",
        new Dictionary<string, string> { ["expression"] = "string" },
        args =>
        {
            var parts = args["expression"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double a = double.Parse(parts[0]), b = double.Parse(parts[2]);
            var result = parts[1] switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => double.NaN };
            return Task.FromResult(result.ToString("0.####"));
        });

    /// <summary>A sandboxed tool: runs a shell command in the sample's container via layer 7.</summary>
    [Tool("bash")]
    public static Tool bash() => new(
        "bash",
        "Run a shell command in the sandbox and return its output.",
        new Dictionary<string, string> { ["cmd"] = "string" },
        async args =>
        {
            var result = await Sandboxes.sandbox().Exec(args["cmd"]);   // -> layer 7 -> container
            return result.Success ? result.Stdout : $"error (exit {result.ReturnCode}): {result.Stderr}";
        });

    /// <summary>
    /// Run every tool call in an assistant message and return the tool
    /// messages to append (Python: execute_tools). Each call is recorded as a
    /// ToolEvent — the transcript again.
    /// </summary>
    public static async Task<List<ChatMessage>> execute_tools(ChatMessage assistant, IReadOnlyList<Tool> tools)
    {
        var results = new List<ChatMessage>();
        foreach (var call in assistant.ToolCalls ?? Array.Empty<ToolCall>())
        {
            var tool = tools.FirstOrDefault(t => t.Name == call.Function);
            var arguments = JsonSerializer.Serialize(call.Arguments);
            Display.Step("L4 tool", $"execute {call.Function}({arguments})");

            string output;
            try
            {
                output = tool is null ? $"error: unknown tool '{call.Function}'" : await tool.Execute(call.Arguments);
            }
            catch (Exception ex)
            {
                output = $"error: {ex.GetType().Name}: {ex.Message}";   // tool errors go back to the model, they do not crash the sample
            }

            Transcript.transcript().Emit(new ToolEvent("L4 tool", call.Function, arguments, output));
            results.Add(ChatMessage.Tool(call.Id, call.Function, output));
        }
        return results;
    }
}
