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
using System.Globalization;
using System.Text.Encodings.Web;
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
    /// <summary>A pure, in-process tool: evaluates an arithmetic expression such as "3.50 + 12.25 * 2".</summary>
    [Tool("calculator")]
    public static Tool calculator() => new(
        "calculator",
        "Evaluate an arithmetic expression using + - * / and parentheses, e.g. '3.50 + 12.25 + 18.00' or '(24 + 86.5) / 5'. Returns the numeric result.",
        new Dictionary<string, string> { ["expression"] = "string" },
        args => Task.FromResult(Arithmetic.Evaluate(args["expression"]).ToString("0.####", CultureInfo.InvariantCulture)));

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
            var arguments = JsonSerializer.Serialize(call.Arguments, Readable);
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

    /// <summary>For the transcript only: keep '+', '>' and quotes as typed instead of \u002B escapes.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>
/// A small recursive-descent evaluator (+ - * / and parentheses), so a real
/// model can send whatever expression it likes. A bad expression throws an
/// ArgumentException, which execute_tools() turns into a tool error message.
/// </summary>
internal static class Arithmetic
{
    public static double Evaluate(string expression)
    {
        var parser = new Parser(expression.Replace("×", "*").Replace("÷", "/").Replace("$", "").Replace(",", ""));
        var value = parser.Expr();
        parser.SkipSpaces();
        if (!parser.AtEnd) throw new ArgumentException($"unexpected '{parser.Rest}' in expression '{expression}'");
        return value;
    }

    private sealed class Parser(string text)
    {
        private int _i;

        public bool AtEnd => _i >= text.Length;
        public string Rest => text[_i..];
        public void SkipSpaces() { while (!AtEnd && char.IsWhiteSpace(text[_i])) _i++; }

        public double Expr()
        {
            var value = Term();
            while (true)
            {
                SkipSpaces();
                if (Take('+')) value += Term();
                else if (Take('-')) value -= Term();
                else return value;
            }
        }

        private double Term()
        {
            var value = Factor();
            while (true)
            {
                SkipSpaces();
                if (Take('*') || Take('x') || Take('X')) value *= Factor();
                else if (Take('/')) value /= Factor();
                else return value;
            }
        }

        private double Factor()
        {
            SkipSpaces();
            if (Take('-')) return -Factor();
            if (Take('+')) return Factor();
            if (Take('('))
            {
                var inner = Expr();
                SkipSpaces();
                if (!Take(')')) throw new ArgumentException($"missing ')' at '{Rest}'");
                return inner;
            }
            var start = _i;
            while (!AtEnd && (char.IsDigit(text[_i]) || text[_i] == '.')) _i++;
            if (start == _i) throw new ArgumentException($"expected a number at '{Rest}'");
            return double.Parse(text[start.._i], CultureInfo.InvariantCulture);
        }

        private bool Take(char c)
        {
            if (AtEnd || text[_i] != c) return false;
            _i++;
            return true;
        }
    }
}
