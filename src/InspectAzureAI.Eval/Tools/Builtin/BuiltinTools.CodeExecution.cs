using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

public static partial class BuiltinTools
{
    /// <summary>The <c>code_execution</c> tool description (the <c>execute</c> docstring, the same text as <c>python()</c>'s).</summary>
    public const string CodeExecutionDescription = SandboxTools.PythonDescription;

    /// <summary>The <c>RuntimeError</c> message of a call when the <c>python</c> fallback is disabled.</summary>
    public const string CodeExecutionFallbackDisabled = "Fallback for `code_execution()` tool requires that `python` be enabled.";

    /// <summary>
    /// Port of <c>code_execution(providers)</c> (<c>tool/_tools/_code_execution.py</c>): lets the model execute
    /// Python code. In Python, providers with native code execution (OpenAI, Anthropic, Google, Grok, Mistral)
    /// run the code on their own servers and every other model falls back to the <c>python()</c> tool in the
    /// sample's sandbox; the <paramref name="providers"/> configuration disables native execution per provider
    /// (or passes provider options) and configures or disables the fallback. The tool's <c>options</c> carry
    /// <c>__internal_tool_type__</c> = "code_execution" and the normalized <c>providers</c> map exactly as Python
    /// does, so a model provider can key on them. Deviation: no model provider of this port executes the tool
    /// server-side (the Azure model-inference route has no code interpreter, and the Anthropic route on Foundry
    /// would need the <c>code-execution-2025-08-25</c> beta plus the <c>bash_code_execution</c> /
    /// <c>text_editor_code_execution</c> result blocks and their verbatim replay, none of which is ported), so the
    /// sandbox fallback is the path every model takes; the task therefore needs a sandbox that runs
    /// <c>python3</c>. A call with the fallback disabled (<c>Python = false</c>) fails the sample with
    /// <see cref="InvalidOperationException"/> (Python's <c>RuntimeError</c>). Calls are shown for approval as a
    /// <c>python</c> code block titled <c>code_execution</c> (<see cref="ToolCallViews.Code"/>).
    /// </summary>
    /// <param name="providers">
    /// Per-provider switches and options (<see cref="CodeExecutionProviders"/>): <c>CodeExecution()</c> (all
    /// native providers, <c>python()</c> as fallback), <c>CodeExecution(new() { Grok = false, OpenAI = false })</c>,
    /// <c>CodeExecution(new() { Python = false })</c>,
    /// <c>CodeExecution(new() { Python = CodeExecutionProviderOption.PythonOptions(TimeSpan.FromSeconds(30), "other") })</c>.
    /// </param>
    public static ToolDef CodeExecution(CodeExecutionProviders? providers = null)
    {
        var normalized = CodeExecutionProviderConfig.Normalize(providers);
        var providerOptions = new JsonObject();
        foreach (var (name, options) in normalized)
        {
            providerOptions[name] = options.DeepClone();
        }

        var toolOptions = new JsonObject
        {
            [WebSearchProviderConfig.InternalToolType] = "code_execution",
            ["providers"] = providerOptions,
        };

        // default implementation is just the python tool
        ToolDef? pythonTool = null;
        if (CodeExecutionProviderConfig.PythonToolOptions(normalized) is { } python)
        {
            pythonTool = SandboxTools.Python(python.Timeout, sandbox: python.Sandbox);
        }

        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["code"] = ToolParam.Of("string", "The python code to execute.") },
            Required = ["code"],
        };
        return new ToolDef("code_execution", CodeExecutionDescription, parameters, async (arguments, cancellationToken) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            if (pythonTool is null)
            {
                throw new InvalidOperationException(CodeExecutionFallbackDisabled);
            }

            return await pythonTool.Execute(arguments, cancellationToken).ConfigureAwait(false);
        })
        {
            // Python's ToolDef(...).as_tool() inside code_execution() leaves parallel unset, which the registry
            // reports as False (unlike the bare python() tool); matched here.
            Parallel = false,
            Options = toolOptions,
            Viewer = ToolCallViews.Code("python", "code", title: "code_execution"),
        };
    }
}
