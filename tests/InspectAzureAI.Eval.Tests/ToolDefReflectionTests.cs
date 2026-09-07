using System.ComponentModel;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// <c>ToolDef.FromMethod</c> / <c>ParseToolInfo</c> against Python's <c>parse_tool_info</c> (<c>tests/tools/test_tool_parse.py</c>,
/// cross-checked with the venv when available), <c>ToolWith</c> (<c>tests/tools/test_tool_with.py</c>) and argument binding.
/// </summary>
public class ToolDefReflectionTests
{
    public enum Unit
    {
        Celsius,
        Fahrenheit,
    }

    public sealed record Point(int X, int Y = 0);

    [Description("A simple function.")]
    private static bool SimpleFunc(
        [Description("An integer parameter")] int a,
        [Description("A string parameter")] string b = "default",
        [Description("An optional flag")] bool? c = null) => a > 0 && b.Length > 0 && c != false;

    [Description("A function with complex types.")]
    private static string ComplexFunc(List<int> a, Dictionary<string, object> b, int? c = null) => $"{a.Count}/{b.Count}/{c}";

    [Description("A function with a record parameter.")]
    private static string PointFunc(Point data) => $"{data.X},{data.Y}";

    private static string NoDescription(int a, string b) => a + b;

    private static async Task<string> WeatherAsync(string city, Unit unit = Unit.Celsius, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return cancellationToken.IsCancellationRequested ? "cancelled" : $"{city}: 21 {unit}";
    }

    private static Task<ToolResult> ContentsAsync() => Task.FromResult(ToolResult.FromContents([new ContentText("a"), new ContentImage("data:image/png;base64,AA==")]));

    private static string Dump(ToolDef tool) => tool.ToInfo().ToJson().ToJsonString();

    private static string PythonToolInfo(string functionName, string definition) => PythonReference.Run($$"""
        import json
        from typing import Any, Dict, List, Optional
        from pydantic import BaseModel
        from inspect_ai.tool._tool_info import parse_tool_info
        from inspect_ai.util._json import json_schema_dump
        {{definition}}
        print(json.dumps(json_schema_dump(parse_tool_info({{functionName}})), separators=(",", ":")))
        """);

    [Fact]
    public void from_method_has_the_parse_tool_info_shape()
    {
        var tool = ToolDef.FromMethod(SimpleFunc, name: "simple_func");

        Assert.Equal("simple_func", tool.Name);
        Assert.Equal("A simple function.", tool.Description);
        Assert.Equal(["a", "b", "c"], tool.Parameters.Properties.Keys);
        Assert.Equal(["integer"], tool.Parameters.Properties["a"].Type);
        Assert.Equal("An integer parameter", tool.Parameters.Properties["a"].Description);
        Assert.Equal("default", tool.Parameters.Properties["b"].Default!.GetValue<string>());
        Assert.Null(tool.Parameters.Properties["c"].Default);
        Assert.NotNull(tool.Parameters.Properties["c"].AnyOf);
        Assert.Equal(["a"], tool.Parameters.Required);
        Assert.Equal(
            """{"name":"simple_func","description":"A simple function.","parameters":{"type":"object","properties":{"a":{"type":"integer","description":"An integer parameter"},"b":{"type":"string","description":"A string parameter","default":"default"},"c":{"description":"An optional flag","anyOf":[{"type":"boolean"},{"type":"null"}]}},"required":["a"],"additionalProperties":false}}""",
            Dump(tool));
    }

    [PythonFact]
    public void from_method_matches_python_parse_tool_info_for_a_simple_function()
    {
        var expected = PythonToolInfo("simple_func", """
            def simple_func(a: int, b: str = "default", c: Optional[bool] = None) -> bool:
                '''A simple function.

                Args:
                    a: An integer parameter
                    b: A string parameter
                    c: An optional flag
                '''
                return True
            """);

        Assert.Equal(expected, Dump(ToolDef.FromMethod(SimpleFunc, name: "simple_func")));
    }

    [PythonFact]
    public void from_method_matches_python_parse_tool_info_for_complex_types()
    {
        var expected = PythonToolInfo("complex_func", """
            def complex_func(a: List[int], b: Dict[str, Any], c: Optional[int] = None) -> str:
                '''A function with complex types.'''
                return ""
            """);

        Assert.Equal(expected, Dump(ToolDef.FromMethod(ComplexFunc, name: "complex_func")));
    }

    [PythonFact]
    public void from_method_matches_python_parse_tool_info_for_a_record_parameter()
    {
        var expected = PythonToolInfo("point_func", """
            class Point(BaseModel):
                X: int
                Y: int = 0

            def point_func(data: Point) -> str:
                '''A function with a record parameter.'''
                return ""
            """);

        Assert.Equal(expected, Dump(ToolDef.FromMethod(PointFunc, name: "point_func")));
    }

    [Fact]
    public void a_method_without_a_description_has_an_empty_one_like_a_missing_docstring()
    {
        var tool = ToolDef.FromMethod(NoDescription);

        Assert.Equal("NoDescription", tool.Name);
        Assert.Equal("", tool.Description);
        Assert.Equal(["a", "b"], tool.Parameters.Required);
    }

    [Fact]
    public void lambdas_need_an_explicit_name_and_cancellation_tokens_are_not_parameters()
    {
        Func<int, int> twice = x => x * 2;

        var ex = Assert.Throws<ArgumentException>(() => ToolDef.FromMethod(twice));
        Assert.Contains("compiler-generated", ex.Message);

        var named = ToolDef.FromMethod(twice, name: "twice", description: "Doubles.");
        Assert.Equal("twice", named.Name);
        Assert.Equal(["x"], named.Parameters.Required);

        var weather = ToolDef.FromMethod(WeatherAsync);
        Assert.Equal(["city", "unit"], weather.Parameters.Properties.Keys);
        Assert.Equal(["Celsius", "Fahrenheit"], weather.Parameters.Properties["unit"].Enum!.Select(e => e!.GetValue<string>()));
        Assert.Equal("Celsius", weather.Parameters.Properties["unit"].Default!.GetValue<string>());
    }

    [Fact]
    public async Task arguments_are_bound_by_name_with_defaults_and_enums_by_name()
    {
        var weather = ToolDef.FromMethod(WeatherAsync);
        var simple = ToolDef.FromMethod(SimpleFunc);
        var point = ToolDef.FromMethod(PointFunc);

        var result = await weather.Execute(new JsonObject { ["city"] = "Oslo", ["unit"] = "Fahrenheit" }, CancellationToken.None);
        var defaulted = await weather.Execute(new JsonObject { ["city"] = "Oslo" }, CancellationToken.None);
        var flag = await simple.Execute(new JsonObject { ["a"] = 1 }, CancellationToken.None);
        var record = await point.Execute(new JsonObject { ["data"] = new JsonObject { ["X"] = 3 } }, CancellationToken.None);

        Assert.Equal("Oslo: 21 Fahrenheit", result.Text);
        Assert.Equal("Oslo: 21 Celsius", defaulted.Text);
        Assert.Equal("True", flag.Text);
        Assert.Equal("3,0", record.Text);
    }

    [Fact]
    public async Task missing_or_unconvertible_arguments_are_tool_parsing_errors()
    {
        var simple = ToolDef.FromMethod(SimpleFunc);

        var missing = await Assert.ThrowsAsync<ToolParsingError>(() => simple.Execute(new JsonObject(), CancellationToken.None));
        var wrong = await Assert.ThrowsAsync<ToolParsingError>(() => simple.Execute(new JsonObject { ["a"] = "not a number" }, CancellationToken.None));
        var nullValue = await Assert.ThrowsAsync<ToolParsingError>(() => simple.Execute(new JsonObject { ["a"] = null }, CancellationToken.None));

        Assert.Equal("Required parameter a not provided to tool call.", missing.Message);
        Assert.StartsWith("Unable to convert '\"not a number\"' to Int32", wrong.Message);
        Assert.Equal("Unable to convert 'null' to Int32", nullValue.Message);
    }

    [Fact]
    public async Task results_are_awaited_and_converted_and_exceptions_propagate_unwrapped()
    {
        var contents = ToolDef.FromMethod(ContentsAsync);
        var thrower = ToolDef.FromMethod((Func<string>)(() => throw new InvalidOperationException("boom")), name: "thrower");
        var number = ToolDef.FromMethod(() => 1.5, name: "number");
        var nothing = ToolDef.FromMethod(() => { }, name: "nothing");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var items = await contents.Execute(new JsonObject(), CancellationToken.None);
        var cancelled = await ToolDef.FromMethod(WeatherAsync).Execute(new JsonObject { ["city"] = "x" }, cts.Token);

        Assert.Equal(2, items.Contents!.Count);
        Assert.Equal("cancelled", cancelled.Text);
        Assert.Equal("1.5", (await number.Execute(new JsonObject(), CancellationToken.None)).Text);
        Assert.Equal("", (await nothing.Execute(new JsonObject(), CancellationToken.None)).Text);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => thrower.Execute(new JsonObject(), CancellationToken.None));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task reflection_tools_run_through_the_tool_executor()
    {
        var weather = ToolDef.FromMethod(WeatherAsync, name: "get_weather");
        var call = new ToolCall("c1", "get_weather", new JsonObject { ["city"] = "Bergen" });

        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("hi"), new ChatMessageAssistant("", toolCalls: [call])], [weather]);

        var message = Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
        Assert.Equal("Bergen: 21 Celsius", message.Text);
        Assert.Null(message.Error);
    }

    [Fact]
    public async Task the_binder_does_not_coerce_string_numbers_or_case_variants()
    {
        var simple = ToolDef.FromMethod(SimpleFunc);

        var stringNumber = await Assert.ThrowsAsync<ToolParsingError>(() => simple.Execute(new JsonObject { ["a"] = "5" }, CancellationToken.None));
        var wrongCase = await Assert.ThrowsAsync<ToolParsingError>(() => simple.Execute(new JsonObject { ["A"] = 1 }, CancellationToken.None));

        Assert.StartsWith("Unable to convert '\"5\"' to Int32", stringNumber.Message);
        Assert.Equal("Required parameter a not provided to tool call.", wrongCase.Message);
    }

    [Fact]
    public async Task the_executor_rejects_invalid_arguments_with_pythons_jsonschema_message()
    {
        var simple = ToolDef.FromMethod(SimpleFunc, name: "simple");
        var point = ToolDef.FromMethod(PointFunc, name: "point");
        var stringNumber = new ToolCall("c1", "simple", new JsonObject { ["a"] = "5" });
        var nestedWrongCase = new ToolCall("c2", "point", new JsonObject { ["data"] = new JsonObject { ["x"] = 3 } });

        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("hi"), new ChatMessageAssistant("", toolCalls: [stringNumber, nestedWrongCase])], [simple, point]);

        Assert.Equal(2, result.Messages.Count);
        var first = Assert.IsType<ChatMessageTool>(result.Messages[0]);
        var second = Assert.IsType<ChatMessageTool>(result.Messages[1]);
        Assert.Equal("parsing", first.Error!.Type);
        Assert.Equal("Found 1 validation errors parsing tool input arguments:\n- '5' is not of type 'integer'", first.Error.Message);
        Assert.Equal("parsing", second.Error!.Type);
        Assert.Equal("Found 2 validation errors parsing tool input arguments:\n- Additional properties are not allowed ('x' was unexpected)\n- 'X' is a required property", second.Error.Message);
    }

    [Fact]
    public void tool_with_overrides_name_description_and_parameter_descriptions_on_a_copy()
    {
        var addition = ToolDef.FromMethod((int x, int y) => x + y, name: "addition", description: "Add two numbers.");

        var renamed = ToolDef.ToolWith(addition, name: "my_addition", description: "my description",
            parameters: new Dictionary<string, string> { ["x"] = "my x description", ["y"] = "my y description" }, parallel: false);

        Assert.Equal("my_addition", renamed.Name);
        Assert.Equal("my description", renamed.Description);
        Assert.Equal("my x description", renamed.Parameters.Properties["x"].Description);
        Assert.Equal("my y description", renamed.Parameters.Properties["y"].Description);
        Assert.False(renamed.Parallel);
        Assert.Equal("addition", addition.Name);
        Assert.Null(addition.Parameters.Properties["x"].Description);
        Assert.True(addition.Parallel);
        Assert.Same(addition.Execute, renamed.Execute);
    }

    [Fact]
    public void tool_with_rejects_unknown_parameters_with_pythons_message()
    {
        var addition = ToolDef.FromMethod((int x, int y) => x + y, name: "addition");

        var ex = Assert.Throws<ArgumentException>(() => ToolDef.ToolWith(addition, parameters: new Dictionary<string, string> { ["p"] = "?" }));

        Assert.StartsWith("tool_with error: no parameter named 'p' (valid parameters are x, y)", ex.Message);
    }
}
