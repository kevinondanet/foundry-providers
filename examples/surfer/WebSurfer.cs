using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.Surfer;

/// <summary>
/// Port of <c>examples/surfer.py</c> <c>web_surfer</c>: a stateful web surfer tool for researching topics. It builds on
/// the <c>web_search()</c> tool to complete sequences of web search actions in service of researching a topic; input
/// can either be requests to do research or questions about previous research. The conversation lives in the sample
/// store (<see cref="WebSurferState"/>, keyed by <c>instance</c>) and each call runs <c>get_model().generate_loop()</c>
/// with <c>web_search()</c>. Deviation: <paramref name="webSearch"/> lets the offline run substitute a canned search
/// tool; Python always calls <c>web_search()</c>.
/// </summary>
public static class WebSurfer
{
    /// <summary>The tool name (<c>@tool def web_surfer</c>).</summary>
    public const string ToolName = "web_surfer";

    /// <summary>The <c>execute</c> docstring's description, as <c>parse_tool_info</c> reads it.</summary>
    public const string Description =
        "Use the web to research a topic.\n\nYou may ask the web surfer any question. These questions can either\nprompt new web searches or be clarifying or follow up questions\nabout previous web searches.";

    /// <summary>The <c>input</c> parameter's docstring.</summary>
    public const string InputDescription = "Message to the web surfer. This can either be a prompt\nto do research or a question about previous research.";

    /// <summary>The <c>clear_history</c> parameter's docstring.</summary>
    public const string ClearHistoryDescription = "Clear memory of previous questions and responses.";

    /// <summary>The system prompt, after <c>dedent</c> (which keeps the leading and trailing newlines).</summary>
    public const string SystemPrompt =
        "\nYou are a helpful assistant that can use a web browser to\nanswer questions. You don't need to answer the questions with\na single web browser request, rather, you can perform searches,\nfollow links, backtrack, and otherwise use the web to its\nfullest capability to help answer the question.\n\nIn some cases questions will be about your previous web searches,\nin those cases you don't always need to use the web search tool\nbut can answer by consulting previous conversation messages.\n";

    /// <summary>
    /// The tool for <paramref name="instance"/> (a fresh short uuid when null, as in Python); <paramref name="webSearch"/>
    /// makes the search tool the surfer's model loop uses (default <c>web_search()</c>).
    /// </summary>
    public static ToolDef Create(string? instance = null, Func<ToolDef>? webSearch = null)
    {
        instance ??= ShortUuid.Generate();
        var search = webSearch ?? (() => BuiltinTools.WebSearch());
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam>
            {
                ["input"] = ToolParam.Of("string", InputDescription),
                ["clear_history"] = ToolParam.Of("boolean", ClearHistoryDescription) with { Default = false },
            },
            Required = ["input"],
        };
        return new ToolDef(ToolName, Description, parameters, (arguments, cancellationToken) => ExecuteAsync(instance, search, arguments, cancellationToken));
    }

    private static async Task<ToolResult> ExecuteAsync(string instance, Func<ToolDef> webSearch, JsonObject arguments, CancellationToken cancellationToken)
    {
        var input = arguments["input"]?.GetValue<string>() ?? throw new ArgumentException("web_surfer requires an 'input' argument");
        var clearHistory = arguments["clear_history"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

        // keep track of message history in the store
        var surferState = Store.StoreAs<WebSurferState>(instance);
        var messages = surferState.Messages;

        // clear history if requested.
        if (clearHistory)
        {
            messages.Clear();
        }

        // provide system prompt if we are at the beginning
        if (messages.Count == 0)
        {
            messages.Add(new ChatMessageSystem(SystemPrompt));
        }

        // append the latest question
        messages.Add(new ChatMessageUser(input));
        surferState.Messages = messages;

        // run tool loop with web search
        var model = SampleContext.Require().ActiveModel;
        var (newMessages, output) = await model.GenerateLoopAsync(messages, tools: [webSearch()], cancellationToken: cancellationToken).ConfigureAwait(false);

        // update state
        messages.AddRange(newMessages);
        surferState.Messages = messages;

        // return response
        return output.Completion;
    }
}
