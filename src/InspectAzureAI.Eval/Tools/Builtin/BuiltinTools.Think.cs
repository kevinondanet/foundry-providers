using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Ports of the built-in tools under <c>tool/_tools/</c>: <see cref="Think"/>, <see cref="WebSearch(WebSearchProviderSpec[])"/>,
/// <see cref="ReadFile"/>, <see cref="ListFiles"/>, <see cref="Grep"/>, <see cref="TodoWrite"/> and <see cref="UpdatePlan"/>.
/// Each factory returns a <see cref="ToolDef"/> whose name, description and parameter schema match the Python
/// <c>ToolInfo</c> exactly; every tool validates its arguments the way Python's <c>validate_tool_input</c> does.
/// </summary>
public static partial class BuiltinTools
{
    /// <summary>The default <c>think</c> tool description (the Python <c>execute</c> docstring, typo included).</summary>
    public const string ThinkDescription =
        "Use the tool to think about something.\n\n"
        + "The will not obtain new information or change the environment, but just append the thought to the log. "
        + "Use it when complex reasoning or some cache memory is needed.";

    /// <summary>The default description of the <c>thought</c> parameter.</summary>
    public const string ThinkThoughtDescription = "A thought to think about.";

    /// <summary>
    /// Port of <c>think()</c> (<c>tool/_tools/_think.py</c>): lets a model take an additional thinking step
    /// that appends the thought to the log without obtaining new information or changing the environment.
    /// Not a substitute for extended thinking; see https://inspect.aisi.org.uk/tools-standard.html#sec-think.
    /// The Python tool-call viewer (markdown rendering of the thought) has no .NET counterpart.
    /// </summary>
    /// <param name="description">Override the default description of the think tool (empty falls back to the default, as in Python).</param>
    /// <param name="thoughtDescription">Override the default description of the thought parameter.</param>
    public static ToolDef Think(string? description = null, string? thoughtDescription = null)
    {
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["thought"] = ToolParam.Of("string", string.IsNullOrEmpty(thoughtDescription) ? ThinkThoughtDescription : thoughtDescription),
            },
            Required = ["thought"],
        };
        return new ToolDef("think", string.IsNullOrEmpty(description) ? ThinkDescription : description, parameters, (arguments, _) =>
        {
            ToolInputValidator.Validate(arguments, parameters);
            return Task.FromResult(ToolResult.Empty);
        })
        { Parallel = true };
    }
}
