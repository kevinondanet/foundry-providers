using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

/// <summary>Port of tests/model/test_parse_tool_call.py.</summary>
public class ParseToolCallTests
{
    private static readonly ToolInfo[] Tools = [Fixtures.TestingTool];

    private static ToolCall Parse(string arguments, IReadOnlyList<ToolInfo>? tools = null) =>
        ToolCallParsing.ParseToolCall("id", "testing_tool", arguments, tools ?? Tools);

    [Fact]
    public void test_parse_tool_call_with_a_dict() =>
        Assert.Equal("I am a dictionary!", Parse("{\"param1\": \"I am a dictionary!\"}").Arguments["param1"]!.GetValue<string>());

    [Fact]
    public void test_parse_string_tool_call_without_a_dict() =>
        Assert.Equal("I am not a dictionary!", Parse("I am not a dictionary!").Arguments["param1"]!.GetValue<string>());

    [Fact]
    public void test_parse_bool_tool_call_without_a_dict() =>
        Assert.True(Parse("True", [Fixtures.TestingToolBool]).Arguments["param1"]!.GetValue<bool>());

    [Fact]
    public void test_parse_single_quotes_in_string_tool_call_without_a_dict() =>
        Assert.Equal("'", Parse("'").Arguments["param1"]!.GetValue<string>());

    [Fact]
    public void test_parse_empty_arguments_with_trailing_quotes()
    {
        var call = Parse("{}\"\"");
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);
    }

    [Fact]
    public void test_parse_arguments_with_trailing_quotes()
    {
        var call = Parse("{\"param1\": \"value\"}\"");
        Assert.Equal("""{"param1":"value"}""", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);
    }

    [Fact]
    public void test_parse_recovered_arguments_logs_arguments()
    {
        ProviderLogger.Reset();
        Parse("{\"param1\": \"value\"}\"\"");
        Assert.Contains(ProviderLogger.Infos, m => m.Contains("{\"param1\": \"value\"}\"\""));
    }

    [Fact]
    public void test_parse_arguments_with_trailing_quotes_and_whitespace()
    {
        var call = Parse("{}\"\n\"");
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);
    }

    [Fact]
    public void test_parse_error_on_trailing_single_quote()
    {
        var call = Parse("{}'");
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
    }

    [Fact]
    public void test_parse_error_on_duplicated_object()
    {
        var call = Parse("{\"param1\": \"first\"}{\"param1\": \"second\"}");
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
    }

    [Fact]
    public void test_parse_duplicate_keys_keep_the_last_value_like_json_loads()
    {
        var call = Parse("{\"param1\": \"first\", \"param1\": \"second\"}");
        Assert.Equal("""{"param1":"second"}""", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);

        var nested = Parse("{\"param1\": {\"a\": [1, {\"b\": 1, \"b\": 2}], \"a\": 3}, \"param1\": {\"a\": 3, \"a\": {\"c\": 1, \"c\": 4}}}\"");
        Assert.Equal("""{"param1":{"a":{"c":4}}}""", nested.Arguments.ToJsonString());
        Assert.Null(nested.ParseError);
    }

    [Theory]
    [InlineData("{\"param1\": NaN}")]
    [InlineData("{\"param1\": Infinity}")]
    [InlineData("{\"param1\": -Infinity}")]
    public void non_standard_float_tokens_are_a_parse_error_unlike_json_loads(string arguments)
    {
        var call = Parse(arguments);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
        Assert.Contains(arguments, call.ParseError);
    }

    [Fact]
    public void test_parse_error_truncation_drops_split_multibyte_sequences()
    {
        // 3-byte characters so that both cut points fall inside a sequence
        var arguments = "{\"param1\": \"" + new string('\u20ac', 10_000) + "\",}invalid";
        var call = Parse(arguments);
        Assert.NotNull(call.ParseError);
        Assert.DoesNotContain('\uFFFD', call.ParseError);
        Assert.Contains("{\"param1\": \"" + new string('\u20ac', 2726 + 2727) + "\",}invalid", call.ParseError);
        Assert.Contains($"(arguments middle-truncated from {System.Text.Encoding.UTF8.GetByteCount(arguments)} bytes)", call.ParseError);

        var truncated = ToolCallParsing.TruncateStringToBytes("a\u00e9", 2)!;
        Assert.Equal("a", truncated.Output);
        Assert.Equal(3, truncated.OriginalBytes);
    }

    [Fact]
    public void test_parse_error_on_truncated_object()
    {
        var call = Parse("{\"param1\": \"value\"");
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
    }

    [Fact]
    public void test_parse_error_preserves_small_arguments()
    {
        var call = Parse("{\"param1\": \"value\",}invalid");
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
        Assert.Contains("{\"param1\": \"value\",}invalid", call.ParseError);
    }

    [Fact]
    public void test_parse_error_truncates_oversized_arguments()
    {
        var arguments = "{\"param1\": \"" + new string('x', 2_000_000) + "\",}invalid";
        var call = Parse(arguments);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
        Assert.True(call.ParseError.Length < 20 * 1024);
        Assert.Contains("middle-truncated", call.ParseError);
        Assert.Equal(1, call.ParseError.Split("{\"param1\": \"x").Length - 1);
        Assert.Contains("\",}invalid", call.ParseError);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(5000)]
    public void test_parse_error_on_deeply_nested_json_arguments(int depth)
    {
        var arguments = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
        var call = Parse(arguments);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.NotNull(call.ParseError);
        Assert.Contains("nesting depth", call.ParseError);
    }

    [Fact]
    public void test_parse_error_on_deeply_nested_json_arguments_with_trailing_quotes()
    {
        var arguments = string.Concat(Enumerable.Repeat("{\"a\":", 300)) + "1" + new string('}', 300) + "\"\"";
        var call = Parse(arguments);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.Contains("nesting depth", call.ParseError);
    }

    [Fact]
    public void test_parse_accepts_bounded_nested_json_arguments()
    {
        var arguments = "{\"param1\":" + string.Concat(Enumerable.Repeat("{\"a\":", 49)) + "1" + new string('}', 50);
        var call = Parse(arguments);
        Assert.Null(call.ParseError);
        Assert.True(call.Arguments.ContainsKey("param1"));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(5000)]
    public void test_parse_error_on_deeply_nested_yaml_arguments(int depth)
    {
        var arguments = new string('[', depth) + "1" + new string(']', depth);
        var call = Parse(arguments);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.Contains("nesting depth", call.ParseError);
    }

    [Fact]
    public void unknown_function_or_parameterless_tool_yields_empty_arguments_without_error()
    {
        var call = ToolCallParsing.ParseToolCall("id", "nope", "Paris", Tools);
        Assert.Equal("{}", call.Arguments.ToJsonString());
        Assert.Null(call.ParseError);
        Assert.Equal("function", call.Type);
    }

    [Theory]
    [InlineData("yes", "true")]
    [InlineData("42", "42")]
    [InlineData("\"Paris\"", "\"Paris\"")]
    [InlineData("1.5", "1.5")]
    [InlineData("null", "null")]
    [InlineData("[1, 2]", "[1,2]")]
    [InlineData("a: b", "{\"a\":\"b\"}")]
    [InlineData("Paris", "\"Paris\"")]
    [InlineData("1_000", "1000")]
    [InlineData("0x10", "16")]
    [InlineData("On", "true")]
    public void yaml_scalar_fallback_matches_pyyaml(string input, string expectedJson)
    {
        var call = Parse(input);
        Assert.Null(call.ParseError);
        Assert.Equal(expectedJson, call.Arguments["param1"]?.ToJsonString() ?? "null");
    }
}
