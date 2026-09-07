using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Tools;

namespace InspectAzureAI.Tests;

/// <summary>
/// The reflection <see cref="JsonSchemaGenerator"/> against <c>json_schema()</c> in <c>util/_json.py</c>
/// (cross-checked with the venv when available) and the <see cref="JsonSchema"/> record itself.
/// </summary>
public class JsonSchemaTests
{
    public enum Color
    {
        Red,
        Green,
    }

    public enum Wire
    {
        [JsonStringEnumMemberName("wire-a")]
        A,
        B,
    }

    public sealed record Address(string Street, string City, int? Zip = null);

    public sealed record Profile(
        [property: Description("Full name")] string Name,
        int Age,
        string? Nickname = null,
        Color Favorite = Color.Red)
    {
        public List<string>? Tags { get; init; }

        [Description("Postal address")]
        public required Address Home { get; init; }

        [JsonIgnore]
        public string Hidden => "";
    }

    public sealed record Node(string Name, List<Node> Children);

    public sealed class Holder
    {
        public string? Optional { get; init; }

        public string Required { get; init; } = "";
    }

    private static string Dump(JsonSchema schema) => schema.ToJson().ToJsonString();

    /// <summary>Python's compact dump of <c>json_schema(expr)</c>.</summary>
    private static string PythonSchema(string typeExpression, string prelude = "") => PythonReference.Run($$"""
        import json
        from datetime import date, datetime, time
        from enum import Enum
        from typing import Any, Dict, List, Optional, Set, Union
        from pydantic import BaseModel, Field
        from inspect_ai.util._json import json_schema, json_schema_dump
        {{prelude}}
        print(json.dumps(json_schema_dump(json_schema({{typeExpression}})), separators=(",", ":")))
        """);

    public static TheoryData<Type, string> PrimitiveCases => new()
    {
        { typeof(int), "int" },
        { typeof(long), "int" },
        { typeof(double), "float" },
        { typeof(decimal), "float" },
        { typeof(string), "str" },
        { typeof(bool), "bool" },
        { typeof(DateTime), "datetime" },
        { typeof(DateTimeOffset), "datetime" },
        { typeof(DateOnly), "date" },
        { typeof(TimeOnly), "time" },
        { typeof(object), "Any" },
        { typeof(int?), "Optional[int]" },
        { typeof(List<int>), "List[int]" },
        { typeof(int[]), "List[int]" },
        { typeof(HashSet<string>), "Set[str]" },
        { typeof(IReadOnlyList<double>), "List[float]" },
        { typeof(Dictionary<string, int>), "Dict[str, int]" },
        { typeof(IReadOnlyDictionary<string, List<bool>>), "Dict[str, List[bool]]" },
        { typeof(List<Dictionary<string, int?>>), "List[Dict[str, Optional[int]]]" },
        { typeof(JsonObject), "dict" },
        { typeof(JsonArray), "list" },
        { typeof((int, string)), "tuple[int, str]" },
        { typeof(Color), "Enum('Color', {'Red': 'Red', 'Green': 'Green'})" },
    };

    [Theory]
    [MemberData(nameof(PrimitiveCases))]
    public void schemas_match_python_json_schema(Type type, string pythonType)
    {
        var expected = PythonReference.Available ? PythonSchema(pythonType) : null;
        var actual = Dump(JsonSchemaGenerator.JsonSchemaOf(type));

        if (expected is not null)
        {
            Assert.Equal(expected, actual);
        }
        else
        {
            Assert.NotEmpty(actual);
        }
    }

    [Fact]
    public void primitive_schemas_have_the_python_shapes()
    {
        Assert.Equal("""{"type":"integer"}""", Dump(JsonSchemaGenerator.JsonSchemaOf<int>()));
        Assert.Equal("""{"type":"number"}""", Dump(JsonSchemaGenerator.JsonSchemaOf<float>()));
        Assert.Equal("""{"type":"string","format":"date-time"}""", Dump(JsonSchemaGenerator.JsonSchemaOf<DateTime>()));
        Assert.Equal("""{"type":"string","format":"date"}""", Dump(JsonSchemaGenerator.JsonSchemaOf<DateOnly>()));
        Assert.Equal("""{"type":"string","format":"time"}""", Dump(JsonSchemaGenerator.JsonSchemaOf<TimeOnly>()));
        Assert.Equal("""{"anyOf":[{"type":"integer"},{"type":"null"}]}""", Dump(JsonSchemaGenerator.JsonSchemaOf<int?>()));
        Assert.Equal("""{"type":"array","items":{"type":"integer"}}""", Dump(JsonSchemaGenerator.JsonSchemaOf<List<int>>()));
        Assert.Equal("""{"type":"object","additionalProperties":{"type":"integer"}}""", Dump(JsonSchemaGenerator.JsonSchemaOf<Dictionary<string, int>>()));
        Assert.Equal("""{"type":"object","additionalProperties":{}}""", Dump(JsonSchemaGenerator.JsonSchemaOf<JsonObject>()));
        Assert.Equal("""{"type":"string","enum":["Red","Green"]}""", Dump(JsonSchemaGenerator.JsonSchemaOf<Color>()));
        Assert.Equal("""{"type":"string","enum":["wire-a","B"]}""", Dump(JsonSchemaGenerator.JsonSchemaOf<Wire>()));
        Assert.Equal("{}", Dump(JsonSchemaGenerator.JsonSchemaOf<object>()));
        Assert.Equal("{}", Dump(JsonSchemaGenerator.JsonSchemaOf<Stream>()));
        Assert.Equal("""{"type":"string","format":"uuid"}""", Dump(JsonSchemaGenerator.JsonSchemaOf<Guid>()));
    }

    [PythonFact]
    public void record_schema_matches_a_pydantic_model()
    {
        var expected = PythonSchema("Profile", """
            class Color(str, Enum):
                Red = "Red"
                Green = "Green"

            class Address(BaseModel):
                Street: str
                City: str
                Zip: Optional[int] = None

            class Profile(BaseModel):
                Name: str = Field(description="Full name")
                Age: int
                Nickname: Optional[str] = None
                Favorite: Color = Color.Red
                Tags: Optional[List[str]] = None
                Home: Address = Field(description="Postal address")
            """);

        Assert.Equal(expected, Dump(JsonSchemaGenerator.JsonSchemaOf<Profile>()));
    }

    [Fact]
    public void record_schema_lists_properties_required_defaults_and_descriptions()
    {
        var schema = JsonSchemaGenerator.JsonSchemaOf<Profile>();

        Assert.Equal(["object"], schema.Type);
        Assert.Equal(false, schema.AdditionalProperties);
        Assert.Equal(["Name", "Age", "Home"], schema.Required);
        Assert.Equal(["Name", "Age", "Nickname", "Favorite", "Tags", "Home"], schema.Properties!.Keys);
        Assert.Equal("Full name", schema.Properties["Name"].Description);
        Assert.Equal("Postal address", schema.Properties["Home"].Description);
        Assert.Equal("Red", schema.Properties["Favorite"].Default!.GetValue<string>());
        Assert.Null(schema.Properties["Nickname"].Default);
        Assert.Equal("""{"anyOf":[{"type":"string"},{"type":"null"}]}""", Dump(schema.Properties["Nickname"]));
        Assert.Equal("""{"anyOf":[{"type":"array","items":{"type":"string"}},{"type":"null"}]}""", Dump(schema.Properties["Tags"]));
        Assert.Equal(["Street", "City"], schema.Properties["Home"].Required);
        Assert.Null(schema.Properties["Home"].AdditionalProperties);
    }

    [Fact]
    public void nullable_reference_annotations_become_optional()
    {
        var schema = JsonSchemaGenerator.JsonSchemaOf<Holder>();

        Assert.Equal("""{"anyOf":[{"type":"string"},{"type":"null"}]}""", Dump(schema.Properties!["Optional"]));
        Assert.Equal("""{"type":"string"}""", Dump(schema.Properties["Required"]));
        Assert.Null(schema.Required);
    }

    [Fact]
    public void recursive_types_break_the_cycle_with_an_empty_schema()
    {
        var schema = JsonSchemaGenerator.JsonSchemaOf<Node>();

        Assert.Equal("{}", Dump(schema.Properties!["Children"].Items!));
        Assert.Equal(["Name", "Children"], schema.Required);
    }

    [Fact]
    public void tool_param_conversions_round_trip()
    {
        var schema = JsonSchemaGenerator.JsonSchemaOf<Profile>();

        ToolParam param = schema;
        JsonSchema back = param;

        Assert.Equal(Dump(schema), param.ToJson().ToJsonString());
        Assert.Equal(Dump(schema), Dump(back));
    }

    [Fact]
    public void set_additional_properties_false_recurses_into_items_properties_and_any_of()
    {
        var schema = new JsonSchema
        {
            Type = ["object"],
            Properties = new Dictionary<string, JsonSchema>
            {
                ["list"] = new() { Type = ["array"], Items = new JsonSchema { Type = ["object"] } },
                ["either"] = new() { AnyOf = [new JsonSchema { Type = ["object"] }, new JsonSchema { Type = ["string"] }] },
            },
        };

        var strict = JsonSchema.SetAdditionalPropertiesFalse(schema);

        Assert.Equal(false, strict.AdditionalProperties);
        Assert.Equal(false, strict.Properties!["list"].Items!.AdditionalProperties);
        Assert.Equal(false, strict.Properties["either"].AnyOf![0].AdditionalProperties);
        Assert.Null(schema.AdditionalProperties);
    }

    [Fact]
    public void json_schema_dump_strips_extended_fields_from_the_generated_schema()
    {
        var schema = new JsonSchema
        {
            Type = ["object"],
            Properties = new Dictionary<string, JsonSchema> { ["name"] = new() { Type = ["string"], MinLength = 1, Pattern = "^a" } },
        };

        var dumped = JsonSchemaDump.Dump(schema.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields);

        Assert.Equal("""{"type":"object","properties":{"name":{"type":"string"}}}""", dumped.ToJsonString());
    }

    [Fact]
    public void of_rejects_unknown_json_types()
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonSchema.Of("text"));
        Assert.Contains("text", ex.Message);
        Assert.Equal("string", JsonSchemaGenerator.PythonTypeToJsonType(null));
        Assert.Throws<ArgumentException>(() => JsonSchemaGenerator.PythonTypeToJsonType("tuple"));
    }
}
