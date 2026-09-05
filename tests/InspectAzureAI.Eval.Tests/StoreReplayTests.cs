using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Store change tracking (<c>StoreEvent</c>), <c>StateEvent</c>s, the <c>json_changes</c> / <c>jsonpatch</c> port,
/// store replay, <c>StoreModel</c> and subtasks (<c>util/_store.py</c>, <c>_util/json.py</c>, <c>util/_subtask.py</c>).
/// </summary>
public sealed class StoreReplayTests
{
    private const string PythonJsonChanges = """
        import json
        from inspect_ai._util.json import json_changes
        cases = json.loads(r'''CASES''')
        out = []
        for before, after in cases:
            changes = json_changes(before, after)
            out.append(None if changes is None else [c.model_dump(by_alias=True, exclude_none=True) for c in changes])
        print(json.dumps(out))
        """;

    /// <summary>Before / after documents: the cases of <c>tests/util/test_json.py</c> plus the jsonpatch behaviours the port relies on.</summary>
    private static readonly (string Name, string Before, string After)[] ReferenceDocuments =
    [
        ("array_shifts", """{"x": ["a", "b"]}""", """{"x": ["c", "a", "d"]}"""),
        ("basic_replace", """{"key": "old_value", "stay": 1}""", """{"key": "new_value", "stay": 1}"""),
        ("insert_fast_path", """{"x": ["a", "b"]}""", """{"x": ["c", "d", "b"]}"""),
        ("remove_shifts", """{"x": ["a", "b", "c"]}""", """{"x": ["a", "z"]}"""),
        ("slow_path_nested", """{"items": [{"id": 1, "status": "active"}, {"id": 2, "status": "active"}]}""", """{"items": [{"id": 99, "status": "new"}, {"id": 1, "status": "inactive"}, {"id": 2, "status": "active"}]}"""),
        ("multiple_arrays", """{"A": [1, 2], "B": [10, 20]}""", """{"A": [99, 1, 88], "B": [10, 20, 30]}"""),
        ("append_root_list", """["a"]""", """["a", "b"]"""),
        ("replace_root_array_item", """["x", "y"]""", """["z", "x"]"""),
        ("nested_arrays_structural", """{"items": [{"tags": ["a", "b", "c"]}, {"tags": ["x", "y"]}]}""", """{"items": [{"tags": ["z", "a", "NEW"]}, {"tags": ["x", "y"]}, {"tags": ["p", "q"]}]}"""),
        ("numeric_keys", """{"tasks": {"0": {"status": "pending"}, "1": {"status": "pending"}}}""", """{"tasks": {"0": {"status": "done"}, "1": {"status": "pending"}, "2": {"status": "new"}}}"""),
        ("move_detection", """{"a": 1, "b": 2}""", """{"b": 2, "c": 1}"""),
        ("list_append_convention", """{"messages": [{"role": "user", "content": "hi"}]}""", """{"messages": [{"role": "user", "content": "hi"}, {"role": "assistant", "content": "hello"}]}"""),
        ("int_float_bool_dumps_inequality", """{"i": 1, "f": 1.0, "t": true, "n": null}""", """{"i": 1.0, "f": 1, "t": 1, "n": null}"""),
        ("list_items_python_equality", """{"l": [1, true, 2.0]}""", """{"l": [1.0, 1, 2]}"""),
        ("nested_removal_and_add", """{"cfg": {"a": {"b": 1}, "c": [1, 2, 3]}}""", """{"cfg": {"a": {"b": 2}, "c": [1, 3], "d": "x"}}"""),
        ("dict_reorder", """{"a": 1, "b": 2}""", """{"b": 2, "a": 1}"""),
        ("root_type_change", """{"a": 1}""", """[1]"""),
        ("escaped_keys", """{"a/b": 1, "c~d": 2}""", """{"a/b": 2, "c~d": 2, "e": 3}"""),
        ("rotate_list", """{"l": ["a", "b", "c"]}""", """{"l": ["b", "c", "a"]}"""),
        ("nested_lists", """{"m": [[1, 2], [3]]}""", """{"m": [[1, 2, 5], [4]]}"""),
        ("store_model_keys", """{"MyModel:x": 5, "MyModel:items": []}""", """{"MyModel:x": 6, "MyModel:items": ["a"], "other": {"k": null}}"""),
        ("value_becomes_null", """{"a": {"b": 1}, "c": 2}""", """{"a": null, "c": 2, "d": null}"""),
        ("remove_from_middle_then_replace", """{"x": [{"n": 1}, {"n": 2}, {"n": 3}]}""", """{"x": [{"n": 1}, {"n": 30}]}"""),
        ("unicode", """{"s": "héllo ✓"}""", """{"s": "wörld ✗"}"""),
    ];

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static JsonArray Sorted(JsonNode? changes) =>
        new(((JsonArray)changes!).Select(c => c!.DeepClone()).OrderBy(c => c.ToJsonString(), StringComparer.Ordinal).ToArray());

    /// <summary>The Python tests' <c>{c.path: c for c in changes}</c>: the last change per path.</summary>
    private static Dictionary<string, JsonChange> ByPath(IReadOnlyList<JsonChange>? changes) =>
        changes!.GroupBy(c => c.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    private static string Dump(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static TaskState State(params ChatMessage[] messages) =>
        new("scripted", 1, 1, "question", messages.Length == 0 ? [new ChatMessageUser("question")] : messages);

    private static (SpanBeginEvent Begin, SpanEndEvent End) Span(string id, string name = "test", string? parentId = null) =>
        (new SpanBeginEvent(id, name, name, parentId), new SpanEndEvent(id));

    private static StoreEvent StoreEventOf(string changes, string? spanId = null) =>
        new(TranscriptEventJson.ToElement(Parse(changes))) { SpanId = spanId };

    // ---------------------------------------------------------------- json_changes

    [PythonFact]
    public void json_changes_match_python_for_every_reference_document()
    {
        var cases = "[" + string.Join(",", ReferenceDocuments.Select(c => $"[{c.Before},{c.After}]")) + "]";
        var expected = Assert.IsType<JsonArray>(Parse(PythonReference.Run(PythonJsonChanges.Replace("CASES", cases, StringComparison.Ordinal))));
        Assert.Equal(ReferenceDocuments.Length, expected.Count);

        for (var i = 0; i < ReferenceDocuments.Length; i++)
        {
            var (name, before, after) = ReferenceDocuments[i];
            var actual = JsonChanges.Diff(Parse(before), Parse(after));
            if (expected[i] is null)
            {
                Assert.True(actual is null, $"{name}: Python reports no changes but the port produced {Dump(actual is null ? null : JsonChanges.ToJson(actual))}");
                continue;
            }

            Assert.True(actual is not null, $"{name}: Python produced {Dump(expected[i])} but the port reports no changes");
            var actualJson = JsonChanges.ToJson(actual);
            // ops within one list keep list order on both sides; ops across dictionary keys follow Python's set
            // iteration (hash-seed dependent), so the comparison is order-insensitive
            Assert.True(JsonValues.DumpsEquals(Sorted(expected[i]), Sorted(actualJson)), $"{name}:\n  python: {Dump(expected[i])}\n  port:   {Dump(actualJson)}");
        }
    }

    [Fact]
    public void json_changes_tracks_replaced_value_through_array_shifts()
    {
        var changes = JsonChanges.Diff(Parse("""{"x": ["a", "b"]}"""), Parse("""{"x": ["c", "a", "d"]}"""))!;

        Assert.Equal(2, changes.Count);
        Assert.Equal((JsonChangeOp.Add, "/x/0", "\"c\""), (changes[0].Op, changes[0].Path, Dump(changes[0].Value)));
        Assert.Equal((JsonChangeOp.Replace, "/x/2", "\"d\"", "\"b\""), (changes[1].Op, changes[1].Path, Dump(changes[1].Value), Dump(changes[1].Replaced)));
        Assert.Null(changes[0].Replaced);
        Assert.Equal("""[{"op":"add","path":"/x/0","value":"c"},{"op":"replace","path":"/x/2","value":"d","replaced":"b"}]""", Dump(JsonChanges.ToJson(changes)));
    }

    [Fact]
    public void json_changes_reference_cases_from_the_python_tests()
    {
        var basic = Assert.Single(JsonChanges.Diff(Parse("""{"key": "old_value", "stay": 1}"""), Parse("""{"key": "new_value", "stay": 1}"""))!);
        Assert.Equal((JsonChangeOp.Replace, "/key", "\"new_value\"", "\"old_value\""), (basic.Op, basic.Path, Dump(basic.Value), Dump(basic.Replaced)));

        var insert = ByPath(JsonChanges.Diff(Parse("""{"x": ["a", "b"]}"""), Parse("""{"x": ["c", "d", "b"]}""")));
        Assert.Equal((JsonChangeOp.Replace, "\"c\"", "\"a\""), (insert["/x/0"].Op, Dump(insert["/x/0"].Value), Dump(insert["/x/0"].Replaced)));
        Assert.Equal((JsonChangeOp.Add, "\"d\""), (insert["/x/1"].Op, Dump(insert["/x/1"].Value)));

        var remove = ByPath(JsonChanges.Diff(Parse("""{"x": ["a", "b", "c"]}"""), Parse("""{"x": ["a", "z"]}""")));
        Assert.Equal((JsonChangeOp.Replace, "\"z\"", "\"b\""), (remove["/x/1"].Op, Dump(remove["/x/1"].Value), Dump(remove["/x/1"].Replaced)));
        Assert.Equal(JsonChangeOp.Remove, remove["/x/2"].Op);

        var nested = ByPath(JsonChanges.Diff(
            Parse("""{"items": [{"id": 1, "status": "active"}, {"id": 2, "status": "active"}]}"""),
            Parse("""{"items": [{"id": 99, "status": "new"}, {"id": 1, "status": "inactive"}, {"id": 2, "status": "active"}]}""")));
        Assert.Equal(("99", "1"), (Dump(nested["/items/0/id"].Value), Dump(nested["/items/0/id"].Replaced)));
        Assert.Equal(("\"new\"", "\"active\""), (Dump(nested["/items/0/status"].Value), Dump(nested["/items/0/status"].Replaced)));
        Assert.Equal(("1", "2"), (Dump(nested["/items/1/id"].Value), Dump(nested["/items/1/id"].Replaced)));
        Assert.Equal(("\"inactive\"", "\"active\""), (Dump(nested["/items/1/status"].Value), Dump(nested["/items/1/status"].Replaced)));
        Assert.Equal((JsonChangeOp.Add, """{"id":2,"status":"active"}"""), (nested["/items/2"].Op, Dump(nested["/items/2"].Value)));

        var arrays = ByPath(JsonChanges.Diff(Parse("""{"A": [1, 2], "B": [10, 20]}"""), Parse("""{"A": [99, 1, 88], "B": [10, 20, 30]}""")));
        Assert.Equal("2", Dump(arrays["/A/2"].Replaced));
        Assert.Equal((JsonChangeOp.Add, "30"), (arrays["/B/2"].Op, Dump(arrays["/B/2"].Value)));

        var append = Assert.Single(JsonChanges.Diff(Parse("""["a"]"""), Parse("""["a", "b"]"""))!);
        Assert.Equal((JsonChangeOp.Add, "/1", "\"b\""), (append.Op, append.Path, Dump(append.Value)));

        Assert.Null(JsonChanges.Diff(Parse("""{"a": [1, 2]}"""), Parse("""{"a": [1, 2]}""")));
        Assert.Null(JsonChanges.Diff(Parse("""{"a": 1, "b": 2}"""), Parse("""{"b": 2, "a": 1}""")));

        var tags = ByPath(JsonChanges.Diff(
            Parse("""{"items": [{"tags": ["a", "b", "c"]}, {"tags": ["x", "y"]}]}"""),
            Parse("""{"items": [{"tags": ["z", "a", "NEW"]}, {"tags": ["x", "y"]}, {"tags": ["p", "q"]}]}""")));
        Assert.Equal((JsonChangeOp.Replace, "\"NEW\"", "\"c\""), (tags["/items/0/tags/2"].Op, Dump(tags["/items/0/tags/2"].Value), Dump(tags["/items/0/tags/2"].Replaced)));

        var numeric = ByPath(JsonChanges.Diff(
            Parse("""{"tasks": {"0": {"status": "pending"}, "1": {"status": "pending"}}}"""),
            Parse("""{"tasks": {"0": {"status": "done"}, "1": {"status": "pending"}, "2": {"status": "new"}}}""")));
        Assert.Equal((JsonChangeOp.Replace, "\"done\"", "\"pending\""), (numeric["/tasks/0/status"].Op, Dump(numeric["/tasks/0/status"].Value), Dump(numeric["/tasks/0/status"].Replaced)));
        Assert.Equal(JsonChangeOp.Add, numeric["/tasks/2"].Op);
    }

    [Fact]
    public void json_changes_detects_moves_and_distinguishes_python_types()
    {
        var move = Assert.Single(JsonChanges.Diff(Parse("""{"a": 1, "b": 2}"""), Parse("""{"b": 2, "c": 1}"""))!);
        Assert.Equal((JsonChangeOp.Move, "/c", "/a"), (move.Op, move.Path, move.From));
        Assert.Equal("""{"op":"move","path":"/c","from":"/a"}""", Dump(move.ToJson()));

        // json.dumps inequality: 1, 1.0 and true are three different dictionary values
        var scalars = JsonChanges.Diff(Parse("""{"i": 1, "f": 1.0, "t": true, "n": null}"""), Parse("""{"i": 1.0, "f": 1, "t": 1, "n": null}"""))!;
        Assert.Equal(["/i", "/f", "/t"], scalars.Select(c => c.Path));
        Assert.All(scalars, c => Assert.Equal(JsonChangeOp.Replace, c.Op));
        Assert.Equal(("1.0", "1"), (Dump(scalars[0].Value), Dump(scalars[0].Replaced)));

        // list items compare with Python ==, where 1 == 1.0 == True
        Assert.Null(JsonChanges.Diff(Parse("""{"l": [1, true, 2.0]}"""), Parse("""{"l": [1.0, 1, 2]}""")));

        var root = Assert.Single(JsonChanges.Diff(Parse("""{"a": 1}"""), Parse("""[1]"""))!);
        Assert.Equal((JsonChangeOp.Replace, "", "[1]", """{"a":1}"""), (root.Op, root.Path, Dump(root.Value), Dump(root.Replaced)));

        var escaped = JsonChanges.Diff(Parse("""{"a/b": 1, "c~d": 2}"""), Parse("""{"a/b": 2, "c~d": 2, "e": 3}"""))!;
        Assert.Equal(["/e", "/a~1b"], escaped.Select(c => c.Path));
    }

    [Fact]
    public void apply_then_diff_round_trips_every_reference_document()
    {
        foreach (var (name, before, after) in ReferenceDocuments)
        {
            var beforeNode = Parse(before);
            var afterNode = Parse(after);
            var changes = JsonChanges.Diff(beforeNode, afterNode);
            var applied = changes is null ? beforeNode : JsonChanges.Apply(beforeNode, changes);
            Assert.True(JsonValues.PythonEquals(afterNode, applied), $"{name}: applying {Dump(changes is null ? null : JsonChanges.ToJson(changes))} gave {Dump(applied)}");
            Assert.Null(JsonChanges.Diff(applied, afterNode));
            // the input document is left untouched unless applied in place
            Assert.True(JsonValues.DumpsEquals(Parse(before), beforeNode), $"{name}: Apply mutated its input");
        }
    }

    [Fact]
    public void json_changes_serialize_and_parse_like_python_including_explicit_nulls()
    {
        var python = """{"op": "replace", "path": "/k", "from": null, "value": "new", "replaced": "old"}""";
        var change = JsonChange.FromJson(Parse(python));
        Assert.Equal((JsonChangeOp.Replace, "/k", "\"new\"", "\"old\""), (change.Op, change.Path, Dump(change.Value), Dump(change.Replaced)));
        Assert.Null(change.From);
        Assert.Equal("""{"op":"replace","path":"/k","value":"new","replaced":"old"}""", Dump(change.ToJson()));

        var nulled = JsonChanges.Diff(Parse("""{"a": {"b": 1}, "c": 2}"""), Parse("""{"a": null, "c": 2, "d": null}"""))!;
        Assert.Equal("""[{"op":"add","path":"/d"},{"op":"replace","path":"/a","replaced":{"b":1}}]""", Dump(JsonChanges.ToJson(nulled)));
        var parsed = JsonChanges.FromJson(JsonChanges.ToJson(nulled));
        Assert.Equal(nulled.Select(c => (c.Op, c.Path)), parsed.Select(c => (c.Op, c.Path)));

        Assert.Throws<JsonException>(() => JsonChange.FromJson(Parse("""{"path": "/k"}""")));
        Assert.Throws<JsonException>(() => JsonChange.FromJson(Parse("""{"op": "bogus", "path": "/k"}""")));
        Assert.Throws<JsonException>(() => JsonChanges.FromJson(Parse("""{"op": "add"}""")));

        var stored = StoreEvent.FromChanges(nulled);
        Assert.Equal(nulled.Select(c => c.ToJson().ToJsonString()), stored.GetChanges().Select(c => c.ToJson().ToJsonString()));
        Assert.Equal(nulled.Count, StateEvent.FromChanges(nulled).GetChanges().Count);
    }

    // ---------------------------------------------------------------- jsonpatch

    [Fact]
    public void json_patch_applies_all_six_operations()
    {
        var doc = Parse("""{"a": {"b": [1, 2]}, "c": "x"}""");
        var patch = JsonChanges.FromJson(Parse("""
            [
              {"op": "add", "path": "/a/b/1", "value": 9},
              {"op": "add", "path": "/a/b/-", "value": 3},
              {"op": "add", "path": "/d", "value": {"e": true}},
              {"op": "remove", "path": "/a/b/0"},
              {"op": "replace", "path": "/c", "value": "y"},
              {"op": "move", "from": "/d/e", "path": "/f"},
              {"op": "copy", "from": "/c", "path": "/g"},
              {"op": "test", "path": "/g", "value": "y"}
            ]
            """));

        var result = JsonPatch.Apply(doc, patch);

        Assert.Equal("""{"a":{"b":[9,2,3]},"c":"y","d":{},"f":true,"g":"y"}""", Dump(result));
        Assert.Equal("""{"a":{"b":[1,2]},"c":"x"}""", Dump(doc));
        Assert.Same(doc, JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Add, "/z") { Value = 1 }], inPlace: true));
        Assert.Equal("1", Dump(doc["z"]));
        Assert.Equal("[1]", Dump(JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Replace, "") { Value = Parse("[1]") }])));
    }

    [Fact]
    public void json_patch_reports_conflicts_invalid_patches_and_failed_tests_like_the_library()
    {
        var doc = Parse("""{"a": [1, 2], "b": {"c": 1}}""");

        Assert.Throws<JsonPatchConflictException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Remove, "/missing")]));
        Assert.Throws<JsonPatchConflictException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Add, "/a/5") { Value = 1 }]));
        Assert.Throws<JsonPatchConflictException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Replace, "/a/2") { Value = 1 }]));
        Assert.Throws<JsonPatchConflictException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Replace, "/missing") { Value = 1 }]));
        Assert.Throws<JsonPatchConflictException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Move, "/b/c/d") { From = "/b" }]));
        Assert.Throws<JsonPatchConflictException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Copy, "/x") { From = "/nope" }]));
        Assert.Throws<InvalidJsonPatchException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Replace, "/a/-") { Value = 1 }]));
        Assert.Throws<InvalidJsonPatchException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Move, "/x")]));
        Assert.Throws<JsonPatchTestFailedException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Test, "/b/c") { Value = 2 }]));
        Assert.Throws<JsonPatchTestFailedException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Test, "/b/missing") { Value = 1 }]));
        Assert.Throws<JsonPointerException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Add, "a") { Value = 1 }]));
        Assert.Throws<JsonPointerException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Add, "/a/x/y") { Value = 1 }]));
        Assert.Throws<JsonPointerException>(() => JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Add, "/b/c/d") { Value = 1 }]));

        // a test op compares with Python ==, so 1 == 1.0 passes
        Assert.NotNull(JsonPatch.Apply(doc, [new JsonChange(JsonChangeOp.Test, "/b/c") { Value = 1.0 }]));

        Assert.Equal(["a", "b/c", "d~e"], new JsonPointer("/a/b~1c/d~0e").Parts);
        Assert.Equal("/a/b~1c/d~0e", new JsonPointer(["a", "b/c", "d~e"]).Path);
        Assert.True(new JsonPointer("/a/b").Contains(new JsonPointer("/a")));
        Assert.False(new JsonPointer("/a").Contains(new JsonPointer("/a/b")));
    }

    [Fact]
    public void to_patch_op_keeps_only_the_members_each_operation_needs()
    {
        var add = JsonChanges.ToPatchOp(new JsonChange(JsonChangeOp.Add, "/a") { Value = null, Replaced = 5 });
        Assert.Equal("""{"op":"add","path":"/a"}""", Dump(add.ToJson()));
        Assert.Null(add.Replaced);

        var replace = JsonChanges.ToPatchOp(new JsonChange(JsonChangeOp.Replace, "/a") { Value = 1, Replaced = 0, From = "/x" });
        Assert.Equal("""{"op":"replace","path":"/a","value":1}""", Dump(replace.ToJson()));

        var move = JsonChanges.ToPatchOp(new JsonChange(JsonChangeOp.Move, "/a") { From = "/b", Value = 1 });
        Assert.Equal("""{"op":"move","path":"/a","from":"/b"}""", Dump(move.ToJson()));

        Assert.Equal("""{"op":"remove","path":"/a"}""", Dump(JsonChanges.ToPatchOp(new JsonChange(JsonChangeOp.Remove, "/a") { Value = 1 }).ToJson()));

        var error = Assert.Throws<ArgumentException>(() => JsonChanges.ToPatchOp(new JsonChange(JsonChangeOp.Copy, "/a")));
        Assert.Contains("requires 'from' field", error.Message);
    }

    // ---------------------------------------------------------------- store_from_events

    [Fact]
    public void store_from_events_replays_root_spans_and_root_level_events_only()
    {
        Assert.Empty(StoreReplay.StoreFromEvents([]).ToDictionary());

        var (begin, end) = Span("span1");
        var single = StoreReplay.StoreFromEvents([begin, StoreEventOf("""[{"op": "add", "path": "/key", "value": "value"}]""", "span1"), end]);
        Assert.Equal("value", single.Get("key"));

        var rootOnly = StoreReplay.StoreFromEvents([StoreEventOf("""[{"op": "add", "path": "/root_key", "value": "root_value"}]""")]);
        Assert.Equal("root_value", rootOnly.Get("root_key"));

        // nested spans: only the outer span's event is applied (it encompasses the inner changes)
        var (outerBegin, outerEnd) = Span("outer");
        var (innerBegin, innerEnd) = Span("inner", parentId: "outer");
        var nested = StoreReplay.StoreFromEvents(
        [
            outerBegin,
            innerBegin,
            StoreEventOf("""[{"op": "add", "path": "/inner", "value": 2}]""", "inner"),
            innerEnd,
            StoreEventOf("""[{"op": "add", "path": "/outer", "value": 1}, {"op": "add", "path": "/inner", "value": 3}]""", "outer"),
            outerEnd,
        ]);
        Assert.Equal(1, nested.Get("outer"));
        Assert.Equal(3, nested.Get("inner"));

        var (begin1, end1) = Span("span1");
        var (begin2, end2) = Span("span2");
        var multiple = StoreReplay.StoreFromEvents(
        [
            begin1, StoreEventOf("""[{"op": "add", "path": "/key1", "value": "value1"}]""", "span1"), end1,
            begin2, StoreEventOf("""[{"op": "add", "path": "/key2", "value": "value2"}]""", "span2"), end2,
        ]);
        Assert.Equal(("value1", "value2"), (multiple.Get("key1"), multiple.Get("key2")));
    }

    [Fact]
    public void store_from_events_applies_replace_remove_nested_and_array_operations()
    {
        var (begin, end) = Span("span1");
        var replaced = StoreReplay.StoreFromEvents([begin, StoreEventOf("""[{"op": "add", "path": "/key", "value": "initial"}, {"op": "replace", "path": "/key", "value": "replaced"}]""", "span1"), end]);
        Assert.Equal("replaced", replaced.Get("key"));

        var removed = StoreReplay.StoreFromEvents([begin, StoreEventOf("""[{"op": "add", "path": "/to_remove", "value": "temp"}, {"op": "add", "path": "/to_keep", "value": "keep"}, {"op": "remove", "path": "/to_remove"}]""", "span1"), end]);
        Assert.False(removed.Contains("to_remove"));
        Assert.Equal("keep", removed.Get("to_keep"));

        var nested = StoreReplay.StoreFromEvents([begin, StoreEventOf("""[{"op": "add", "path": "/config", "value": {}}, {"op": "add", "path": "/config/nested", "value": {"deep": "value"}}, {"op": "add", "path": "/config/nested/deep2", "value": "value2"}]""", "span1"), end]);
        var config = Assert.IsType<Dictionary<string, object?>>(nested.Get("config"));
        var inner = Assert.IsType<Dictionary<string, object?>>(config["nested"]);
        Assert.Equal(("value", "value2"), (inner["deep"], inner["deep2"]));

        var arrays = StoreReplay.StoreFromEvents([begin, StoreEventOf("""[{"op": "add", "path": "/items", "value": []}, {"op": "add", "path": "/items/0", "value": "first"}, {"op": "add", "path": "/items/1", "value": "second"}, {"op": "add", "path": "/items/-", "value": "last"}]""", "span1"), end]);
        Assert.Equal(["first", "second", "last"], Assert.IsType<List<object?>>(arrays.Get("items")).Cast<string>());

        // Python's _json_change_to_patch_op rejects a move/copy without 'from'; a non-object result is a conflict
        Assert.Throws<ArgumentException>(() => StoreReplay.StoreFromEvents([StoreEventOf("""[{"op": "move", "path": "/a"}]""")]));
        Assert.Throws<JsonPatchConflictException>(() => StoreReplay.StoreFromEvents([StoreEventOf("""[{"op": "replace", "path": "", "value": 1}]""")]));
        Assert.Throws<JsonPatchConflictException>(() => StoreReplay.StoreFromEvents([StoreEventOf("""[{"op": "remove", "path": "/missing"}]""")]));
    }

    [Fact]
    public void store_from_events_as_narrows_to_the_model_and_instance()
    {
        var (begin, end) = Span("span1");
        var basic = StoreReplay.StoreFromEventsAs<SampleStoreModel>([begin, StoreEventOf("""[{"op": "add", "path": "/SampleStoreModel:counter", "value": 42}, {"op": "add", "path": "/SampleStoreModel:message", "value": "hello"}]""", "span1"), end]);
        Assert.Equal((42, "hello"), (basic.Counter, basic.Message));
        Assert.Empty(basic.Items);
        Assert.Null(basic.Instance);

        var defaults = StoreReplay.StoreFromEventsAs<SampleStoreModel>([begin, StoreEventOf("""[{"op": "add", "path": "/SampleStoreModel:counter", "value": 10}]""", "span1"), end]);
        Assert.Equal((10, ""), (defaults.Counter, defaults.Message));
        Assert.Empty(defaults.Items);

        var instance = StoreReplay.StoreFromEventsAs<SampleStoreModel>(
            [begin, StoreEventOf("""[{"op": "add", "path": "/SampleStoreModel:instance1:counter", "value": 100}, {"op": "add", "path": "/SampleStoreModel:instance1:message", "value": "inst1"}, {"op": "add", "path": "/SampleStoreModel:counter", "value": 1}]""", "span1"), end],
            "instance1");
        Assert.Equal((100, "inst1", "instance1"), (instance.Counter, instance.Message, instance.Instance));
        Assert.Equal(["SampleStoreModel:instance1:counter", "SampleStoreModel:instance1:message", "SampleStoreModel:instance1:items"], instance.Store.Keys);

        var others = StoreReplay.StoreFromEventsAs<SampleStoreModel>([begin, StoreEventOf("""[{"op": "add", "path": "/SampleStoreModel:counter", "value": 5}, {"op": "add", "path": "/SampleStoreModel:other:counter", "value": 7}, {"op": "add", "path": "/OtherModel:counter", "value": 999}, {"op": "add", "path": "/plain_key", "value": "ignored"}]""", "span1"), end]);
        Assert.Equal(5, others.Counter);
        Assert.Equal(["SampleStoreModel:counter", "SampleStoreModel:message", "SampleStoreModel:items"], others.Store.Keys);

        // replayed values (plain lists of objects) are coerced to the declared field type and written back
        var complex = StoreReplay.StoreFromEventsAs<SampleStoreModel>([begin, StoreEventOf("""[{"op": "add", "path": "/SampleStoreModel:items", "value": ["a", "b", "c"]}]""", "span1"), end]);
        Assert.Equal(["a", "b", "c"], complex.Items);
        Assert.IsType<List<string>>(complex.Store.Get("SampleStoreModel:items"));
    }

    [Fact]
    public async Task store_from_events_reconstructs_a_store_recorded_through_spans()
    {
        using var scope = new SampleContextScope();
        var store = scope.Context.Store;
        var transcript = scope.Transcript;
        using (transcript.Span("solvers"))
        {
            using (transcript.Span("step1", "solver"))
            {
                store.Set("count", 1);
                store.Set("items", new List<object?> { "a" });
                await Subtask.RunAsync("sub", _ => Task.FromResult(1));
            }

            using (transcript.Span("step2", "solver"))
            {
                store.Set("count", 2);
                ((List<object?>)store.Get("items")!).Add("b");
                store.Set("nested", new Dictionary<string, object?> { ["k"] = new List<object?> { 1, 2.5, null } });
                store.Delete("nested");
                store.Set("nested", new Dictionary<string, object?> { ["k"] = new List<object?> { 1, 2.5, null }, ["s"] = "x" });
            }
        }

        using (transcript.Span("scorers"))
        {
            store.Set("SampleStoreModel:counter", 9);
        }

        var replayed = StoreReplay.StoreFromEvents(transcript.Events);
        Assert.True(JsonValues.DumpsEquals(Jsonable.FromStore(store), Jsonable.FromStore(replayed)), Dump(Jsonable.FromStore(replayed)));
        Assert.Equal(9, StoreReplay.StoreFromEventsAs<SampleStoreModel>(transcript.Events).Counter);
    }

    // ---------------------------------------------------------------- StoreEvent via spans

    [Fact]
    public void spans_record_store_events_with_the_span_id_before_the_span_end()
    {
        using var scope = new SampleContextScope();
        var store = scope.Context.Store;
        var transcript = scope.Transcript;

        using (transcript.Span("outer"))
        {
            store.Set("a", 1);
            using (transcript.Span("inner"))
            {
                store.Set("b", new List<object?> { "x" });
            }

            using (transcript.Span("unchanged"))
            {
                store.Set("b", new List<object?> { "x" });
            }

            ((List<object?>)store.Get("b")!).Add("y");
        }

        var events = transcript.Events;
        Assert.Equal(["span_begin", "span_begin", "store", "span_end", "span_begin", "span_end", "store", "span_end"], events.Select(e => e.Event));
        var outer = Assert.IsType<SpanBeginEvent>(events[0]);
        var inner = Assert.IsType<SpanBeginEvent>(events[1]);
        var innerStore = Assert.IsType<StoreEvent>(events[2]);
        var outerStore = Assert.IsType<StoreEvent>(events[6]);
        Assert.Equal(inner.Id, innerStore.SpanId);
        Assert.Equal(outer.Id, outerStore.SpanId);
        Assert.Equal("""[{"op":"add","path":"/b","value":["x"]}]""", Dump(JsonChanges.ToJson(innerStore.GetChanges())));
        // the outer span reports everything that changed inside it, with an appended item as an add at the next index
        Assert.Equal("""[{"op":"add","path":"/a","value":1},{"op":"add","path":"/b","value":["x","y"]}]""", Dump(JsonChanges.ToJson(outerStore.GetChanges())));

        using (transcript.Span("append"))
        {
            ((List<object?>)store.Get("b")!).Add("z");
            store.Set("a", 2);
        }

        var append = Assert.IsType<StoreEvent>(transcript.Events[^2]);
        Assert.Equal("""[{"op":"replace","path":"/a","value":2,"replaced":1},{"op":"add","path":"/b/2","value":"z"}]""", Dump(JsonChanges.ToJson(append.GetChanges())));
    }

    [Fact]
    public void store_changes_track_and_between_work_with_explicit_stores()
    {
        var transcript = new Transcript();
        var store = new Store();
        using (transcript.Span("no context"))
        {
            store.Set("a", 1);
        }

        Assert.Equal(["span_begin", "span_end"], transcript.Events.Select(e => e.Event));

        using (StoreChanges.Track(store, transcript))
        {
            store.Set("a", 2);
        }

        var tracked = Assert.IsType<StoreEvent>(Assert.Single(transcript.Events, e => e is StoreEvent));
        Assert.Equal("""[{"op":"replace","path":"/a","value":2,"replaced":1}]""", Dump(JsonChanges.ToJson(tracked.GetChanges())));

        var before = new Store(new Dictionary<string, object?> { ["x"] = 1 });
        var after = new Store(new Dictionary<string, object?> { ["x"] = 1, ["y"] = "z" });
        Assert.Null(StoreChanges.Between(before, before));
        Assert.Equal("""[{"op":"add","path":"/y","value":"z"}]""", Dump(JsonChanges.ToJson(StoreChanges.Between(before, after)!)));
        StoreChanges.Track().Dispose();
    }

    // ---------------------------------------------------------------- StateEvent

    [Fact]
    public void solver_transcript_records_a_state_event_with_the_changes_in_python_key_order()
    {
        var transcript = new Transcript();
        var state = State();
        var solverTranscript = new SolverTranscript(state, transcript);
        var snapshot = Jsonable.FromState(state);
        Assert.Equal(["messages", "tools", "tool_choice", "store", "output", "completed", "metadata"], snapshot.Select(pair => pair.Key));
        Assert.True(snapshot.ContainsKey("tool_choice"));
        Assert.Null(snapshot["tool_choice"]);

        state.Messages.Add(new ChatMessageAssistant("hi"));
        state.Metadata["foo"] = "bar";
        state.Store.Set("k", 1);
        state.Completed = true;
        state.Tools.Add(new ToolDef("echo", "Echoes", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("x")));
        solverTranscript.Complete(state);

        var stateEvent = Assert.IsType<StateEvent>(Assert.Single(transcript.Events));
        var changes = stateEvent.GetChanges();
        Assert.Equal(["/messages/1", "/tools/0", "/store/k", "/completed", "/metadata/foo"], changes.Select(c => c.Path));
        Assert.Equal([JsonChangeOp.Add, JsonChangeOp.Add, JsonChangeOp.Add, JsonChangeOp.Replace, JsonChangeOp.Add], changes.Select(c => c.Op));
        Assert.Equal("assistant", changes[0].Value!["role"]!.GetValue<string>());
        Assert.Equal("hi", changes[0].Value!["content"]!.GetValue<string>());
        Assert.Equal("echo", changes[1].Value!["name"]!.GetValue<string>());
        Assert.Equal(("true", "false"), (Dump(changes[3].Value), Dump(changes[3].Replaced)));

        // nothing changed: nothing recorded
        new SolverTranscript(state, transcript).Complete(state);
        Assert.Single(transcript.Events);
    }

    [Fact]
    public async Task chains_record_a_solver_span_and_state_event_per_step_like_python()
    {
        using var scope = new SampleContextScope();
        Solver first = (state, _, _) =>
        {
            state.Metadata["step"] = 1;
            state.Store.Set("s", 1);
            SampleContext.Require().Store.Set("ambient", 1);
            return Task.FromResult(state);
        };
        Solver second = (state, _, _) =>
        {
            state.Messages.Add(new ChatMessageAssistant("done"));
            return Task.FromResult(state);
        };
        Solver third = (state, _, _) => Task.FromResult(state);

        var result = await Solvers.Chain(first, Solvers.UseTools([]), second, third)(State(), GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Equal(["done"], result.Messages.Skip(1).Select(m => m.Text));
        var events = scope.Transcript.Events;
        Assert.Equal(
            ["span_begin", "state", "store", "span_end", "span_begin", "span_end", "span_begin", "state", "span_end", "span_begin", "span_end"],
            events.Select(e => e.Event));
        var spans = events.OfType<SpanBeginEvent>().ToList();
        Assert.All(spans, span => Assert.Equal("solver", span.Type));
        Assert.Equal("use_tools", spans[1].Name);
        Assert.Equal(spans[0].Id, events[1].SpanId);
        Assert.Equal(spans[0].Id, events[2].SpanId);
        Assert.Equal(spans[2].Id, events[7].SpanId);
        Assert.Equal(["/store/s", "/metadata/step"], Assert.IsType<StateEvent>(events[1]).GetChanges().Select(c => c.Path));
        Assert.Equal(["/ambient"], Assert.IsType<StoreEvent>(events[2]).GetChanges().Select(c => c.Path));
        Assert.Equal(["/messages/1"], Assert.IsType<StateEvent>(events[7]).GetChanges().Select(c => c.Path));

        Assert.Equal("chain", Solvers.LogName(Solvers.Chain(first)));
        Assert.True(Solvers.IsChain(Solvers.Chain(first)));
        Assert.False(Solvers.IsChain(first));
    }

    // ---------------------------------------------------------------- StoreModel

    private sealed class SampleStoreModel : StoreModel
    {
        public int Counter { get => Get(0); set => Set(value); }

        public string Message { get => Get(""); set => Set(value); }

        public List<string> Items { get => Get(new List<string>()); set => Set(value); }
    }

    private sealed class MyModel : StoreModel
    {
        public int X { get => Get(5); set => Set(value); }

        public string Y { get => Get("default_y"); set => Set(value); }

        public double Z { get => Get(1.23); set => Set(value); }

        public MyModel? Nested { get => Get<MyModel?>(null); set => Set(value); }
    }

    private sealed class Step
    {
        public Dictionary<string, object?> Response { get; init; } = [];

        public List<Dictionary<string, object?>> Results { get; init; } = [];
    }

    private sealed class Trajectory : StoreModel
    {
        public List<Step> Steps { get => Get(new List<Step>()); set => Set(value); }
    }

    private sealed class AutoPropertyModel : StoreModel
    {
        public int A { get; set; }
    }

    [Fact]
    public void store_model_writes_defaults_on_binding_and_syncs_with_the_store()
    {
        var store = new Store();
        var model = store.As<MyModel>();

        Assert.Equal(["MyModel:x", "MyModel:y", "MyModel:z", "MyModel:nested"], store.Keys);
        Assert.Equal((5, "default_y", 1.23), (model.X, model.Y, model.Z));
        Assert.Equal(5, store.Get("MyModel:x"));

        model.X = 42;
        Assert.Equal(42, store.Get("MyModel:x"));
        store.Set("MyModel:y", "behind the scenes");
        Assert.Equal("behind the scenes", model.Y);

        // a deleted key falls back to the model's own last value (Python's __dict__), without re-adding it
        store.Delete("MyModel:x");
        Assert.Equal(42, model.X);
        Assert.False(store.Contains("MyModel:x"));
        store.Delete("MyModel:y");
        Assert.Equal("behind the scenes", model.Y);

        var fresh = new Store(new Dictionary<string, object?> { ["MyModel:x"] = 9 });
        Assert.Equal(9, StoreModel.Create<MyModel>(fresh).X);
        Assert.Equal(9, fresh.Get("MyModel:x"));

        Assert.Equal(new Dictionary<string, object?> { ["x"] = 42, ["y"] = "behind the scenes", ["z"] = 1.23, ["nested"] = null }, model.ToDictionary());
        Assert.Equal("""{"x":42,"y":"behind the scenes","z":1.23,"nested":null}""", Dump(model.ToJson()));
        Assert.Equal("MyModel:some_long_name", model.NamespacedName("SomeLongName"));
    }

    [Fact]
    public void store_model_instances_share_a_store_and_instance_names_isolate_them()
    {
        var store = new Store();
        var model1 = StoreModel.Create<MyModel>(store);
        var model2 = StoreModel.Create<MyModel>(store);
        model1.X = 42;
        Assert.Equal(42, model2.X);
        model2.Y = "shared";
        Assert.Equal("shared", model1.Y);

        var m1 = StoreModel.Create<MyModel>(store, "m1");
        var m2 = StoreModel.Create<MyModel>(store, "m2");
        m1.X = 42;
        Assert.Equal(5, m2.X);
        m2.Y = "shared";
        Assert.Equal("default_y", m1.Y);
        Assert.Equal(("m1", "MyModel:m1:x"), (m1.Instance, m1.NamespacedName("X")));
        Assert.Equal(42, store.Get("MyModel:m1:x"));
        Assert.Equal(5, store.Get("MyModel:m2:x"));
    }

    [Fact]
    public void store_model_coerces_replayed_values_and_rejects_impossible_ones()
    {
        var store = new Store(new Dictionary<string, object?>
        {
            ["Trajectory:steps"] = new List<object?>
            {
                new Dictionary<string, object?> { ["response"] = new Dictionary<string, object?> { ["foo"] = "bar" }, ["results"] = new List<object?> { new Dictionary<string, object?> { ["n"] = 1 } } },
            },
        });
        var trajectory = store.As<Trajectory>();
        var step = Assert.Single(trajectory.Steps);
        Assert.Equal("bar", step.Response["foo"]);
        Assert.Equal(1, Assert.Single(step.Results)["n"]);
        Assert.Same(trajectory.Steps, store.Get("Trajectory:steps"));

        trajectory.Steps.Add(new Step { Response = new Dictionary<string, object?> { ["x"] = 1 } });
        Assert.Equal(2, ((List<Step>)store.Get("Trajectory:steps")!).Count);

        var longs = new Store(new Dictionary<string, object?> { ["MyModel:x"] = 7L, ["MyModel:z"] = 2 });
        var coerced = longs.As<MyModel>();
        Assert.Equal((7, 2.0), (coerced.X, coerced.Z));
        Assert.IsType<int>(longs.Get("MyModel:x"));

        var bad = new Store(new Dictionary<string, object?> { ["MyModel:x"] = "abc" });
        Assert.Throws<StoreModelException>(() => bad.As<MyModel>());
        var nulled = new Store(new Dictionary<string, object?> { ["MyModel:x"] = null });
        Assert.Throws<StoreModelException>(() => nulled.As<MyModel>());

        var model = new Store().As<MyModel>();
        var embed = Assert.Throws<ArgumentException>(() => model.Nested = new Store().As<MyModel>());
        Assert.Contains("may not embed a StoreModel", embed.Message);
        Assert.Throws<InvalidOperationException>(() => new MyModel().X);
        Assert.Throws<InvalidOperationException>(() => new Store().As<AutoPropertyModel>());
    }

    [Fact]
    public void store_as_binds_to_the_ambient_or_state_store_and_changes_reach_the_store_event()
    {
        Assert.Throws<InvalidOperationException>(() => Store.StoreAs<MyModel>());

        using var scope = new SampleContextScope();
        var state = State();
        using (scope.Transcript.Span("solver", "solver"))
        {
            var ambient = Store.StoreAs<MyModel>("a");
            ambient.X = 1;
            var fromState = state.StoreAs<MyModel>();
            fromState.Y = "state";
        }

        Assert.Equal(1, scope.Context.Store.Get("MyModel:a:x"));
        Assert.Equal("state", state.Store.Get("MyModel:y"));
        var storeEvent = Assert.IsType<StoreEvent>(Assert.Single(scope.Transcript.Events, e => e is StoreEvent));
        Assert.Equal(["/MyModel:a:x", "/MyModel:a:y", "/MyModel:a:z", "/MyModel:a:nested"], storeEvent.GetChanges().Select(c => c.Path));
        Assert.Equal("""{"op":"add","path":"/MyModel:a:x","value":1}""", Dump(storeEvent.GetChanges()[0].ToJson()));
        Assert.Equal("""{"op":"add","path":"/MyModel:a:nested"}""", Dump(storeEvent.GetChanges()[3].ToJson()));
    }

    // ---------------------------------------------------------------- subtasks

    [Fact]
    public async Task subtasks_get_a_fresh_store_and_record_a_nested_subtask_event_updated_with_the_result()
    {
        using var scope = new SampleContextScope();
        var parentStore = scope.Context.Store;
        parentStore.Set("x", 42);
        var transcript = scope.Transcript;

        async Task<int> TimesTwo(int input)
        {
            var store = SampleContext.Require().Store;
            Assert.NotSame(parentStore, store);
            Assert.Equal(0, store.Get("x", 0));
            store.Set("x", 84);
            transcript.Info("inside", input);
            await Task.Yield();
            return input * 2;
        }

        int result;
        using (transcript.Span("solver", "solver"))
        {
            result = await Subtask.RunAsync("times_two", _ => TimesTwo(1), new Dictionary<string, object?> { ["input"] = 1 });
            result += await Subtask.RunAsync("times_two", _ => TimesTwo(result), new Dictionary<string, object?> { ["input"] = result });
        }

        Assert.Equal(6, result);
        Assert.Equal(42, parentStore.Get("x"));
        Assert.Same(scope.Context, SampleContext.Current);
        Assert.Null(transcript.CurrentSpanId);

        var events = transcript.Events;
        Assert.Equal(
            ["span_begin", "span_begin", "subtask", "info", "store", "span_end", "span_begin", "subtask", "info", "store", "span_end", "span_end"],
            events.Select(e => e.Event));
        var solverSpan = Assert.IsType<SpanBeginEvent>(events[0]);
        var subtaskSpan = Assert.IsType<SpanBeginEvent>(events[1]);
        Assert.Equal(("times_two", "subtask", solverSpan.Id), (subtaskSpan.Name, subtaskSpan.Type, subtaskSpan.ParentId));
        var subtask = Assert.IsType<SubtaskEvent>(events[2]);
        Assert.Equal(subtaskSpan.Id, subtask.SpanId);
        Assert.Equal(subtaskSpan.Id, events[3].SpanId);
        Assert.Equal("times_two", subtask.Name);
        Assert.Null(subtask.Type);
        Assert.Equal("""{"input":1}""", Dump(subtask.Input));
        Assert.Equal("2", Dump(subtask.Result));
        Assert.Null(subtask.Pending);
        Assert.NotNull(subtask.Completed);
        Assert.True(subtask.Completed >= subtask.Timestamp);
        Assert.True(subtask.WorkingTime >= 0);
        Assert.True(subtask.WorkingStart > 0);
        Assert.Equal("""[{"op":"add","path":"/x","value":84}]""", Dump(JsonChanges.ToJson(Assert.IsType<StoreEvent>(events[4]).GetChanges())));
        Assert.Equal("""{"input":2}""", Dump(Assert.IsType<SubtaskEvent>(events[7]).Input));
        Assert.Equal("4", Dump(Assert.IsType<SubtaskEvent>(events[7]).Result));
        // the solver span's store event only covers the parent store, which the subtasks left alone
        Assert.DoesNotContain(events.Skip(11), e => e is StoreEvent);
    }

    [Fact]
    public async Task subtasks_leave_the_event_pending_and_restore_context_on_failure_or_cancellation()
    {
        using var scope = new SampleContextScope();
        var explicitStore = new Store();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Subtask.RunAsync("boom", _ =>
        {
            Assert.Same(explicitStore, SampleContext.Require().Store);
            explicitStore.Set("k", "v");
            throw new InvalidOperationException("boom");
        }, store: explicitStore, type: "custom"));

        using var cts = new CancellationTokenSource();
        var pending = Subtask.RunAsync("wait", async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 1; }, cancellationToken: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Same(scope.Context, SampleContext.Current);
        Assert.Null(scope.Transcript.CurrentSpanId);
        var events = scope.Transcript.Events;
        Assert.Equal(["span_begin", "subtask", "store", "span_end", "span_begin", "subtask", "span_end"], events.Select(e => e.Event));
        var failed = Assert.IsType<SubtaskEvent>(events[1]);
        Assert.Equal(("boom", "custom", true), (failed.Name, failed.Type, failed.Pending));
        Assert.Null(failed.Result);
        Assert.Null(failed.Completed);
        Assert.Equal("""[{"op":"add","path":"/k","value":"v"}]""", Dump(JsonChanges.ToJson(Assert.IsType<StoreEvent>(events[2]).GetChanges())));
        Assert.True(Assert.IsType<SubtaskEvent>(events[5]).Pending);

    }

    [Fact]
    public async Task subtasks_simply_run_without_a_sample_context()
    {
        Assert.Null(SampleContext.Current);
        var seen = false;
        Assert.Equal(3, await Subtask.RunAsync("plain", _ => Task.FromResult(3)));
        await Subtask.RunAsync("void", _ =>
        {
            seen = true;
            return Task.CompletedTask;
        });
        Assert.True(seen);
        Assert.Null(SampleContext.Current);
    }

    [Fact]
    public async Task forks_run_as_fork_subtasks_with_a_solver_span_and_state_event_inside()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("reply")));
        var generate = GenerateLoop.Create(scope.Model);
        Solver branch = (state, _, _) =>
        {
            state.Metadata["forked"] = true;
            SampleContext.Require().Store.Set("branch", 1);
            return Task.FromResult(state);
        };
        var original = State();

        var forked = await Solvers.Fork(original, branch, generate);

        Assert.True((bool)forked.Metadata["forked"]!);
        Assert.False(original.Metadata.ContainsKey("forked"));
        Assert.Equal(1, forked.Store.Get("branch"));
        Assert.False(original.Store.Contains("branch"));
        Assert.False(scope.Context.Store.Contains("branch"));

        var events = scope.Transcript.Events;
        Assert.Equal(["span_begin", "subtask", "span_begin", "state", "store", "span_end", "store", "span_end"], events.Select(e => e.Event));
        var subtaskSpan = Assert.IsType<SpanBeginEvent>(events[0]);
        Assert.Equal(("fork", "subtask"), (subtaskSpan.Name, subtaskSpan.Type));
        var subtask = Assert.IsType<SubtaskEvent>(events[1]);
        Assert.Equal(("fork", "fork", subtaskSpan.Id), (subtask.Name, subtask.Type, subtask.SpanId));
        Assert.Null(subtask.Pending);
        Assert.Null(subtask.Result);
        Assert.Equal("{}", Dump(subtask.Input));
        var solverSpan = Assert.IsType<SpanBeginEvent>(events[2]);
        Assert.Equal(("solver", subtaskSpan.Id), (solverSpan.Type, solverSpan.ParentId));
        Assert.Equal(["/store/branch", "/metadata/forked"], Assert.IsType<StateEvent>(events[3]).GetChanges().Select(c => c.Path));

        var chained = await Solvers.Fork(State(), Solvers.Chain(branch), generate);
        Assert.True((bool)chained.Metadata["forked"]!);
        Assert.Equal("chain", Assert.IsType<SubtaskEvent>(Assert.Single(scope.Transcript.Events.Skip(8), e => e is SubtaskEvent)).Name);
    }

    [Fact]
    public void transcript_record_stamps_and_update_replaces_in_place()
    {
        var transcript = new Transcript();
        SubtaskEvent recorded;
        using (transcript.Span("s"))
        {
            recorded = transcript.Record(new SubtaskEvent("sub", new JsonObject()) { Pending = true });
            transcript.Info("after");
        }

        Assert.NotNull(recorded.SpanId);
        Assert.True(recorded.WorkingStart > 0);
        transcript.Update(recorded with { Pending = null, Result = JsonValue.Create(1) });

        var events = transcript.Events;
        Assert.Equal(["span_begin", "subtask", "info", "span_end"], events.Select(e => e.Event));
        var updated = Assert.IsType<SubtaskEvent>(events[1]);
        Assert.Equal((recorded.Uuid, recorded.SpanId, "1"), (updated.Uuid, updated.SpanId, Dump(updated.Result)));
        Assert.Null(updated.Pending);

        Assert.Throws<InvalidOperationException>(() => transcript.Update(new InfoEvent("x", null)));
        Assert.Throws<InvalidOperationException>(() => transcript.Update(new InfoEvent("x", null) { Uuid = null }));
    }

    // ---------------------------------------------------------------- event tree

    [Fact]
    public void event_tree_nests_spans_and_flattens_back_in_transcript_order()
    {
        var (outerBegin, outerEnd) = Span("outer");
        var (innerBegin, innerEnd) = Span("inner", parentId: "outer");
        var rootInfo = new InfoEvent("root", null);
        var innerInfo = new InfoEvent("inner", null) { SpanId = "inner" };
        var outerInfo = new InfoEvent("outer", null) { SpanId = "outer" };
        var orphanInfo = new InfoEvent("orphan", null) { SpanId = "missing" };
        var strayEnd = new SpanEndEvent("missing");
        TranscriptEvent[] events = [rootInfo, outerBegin, innerBegin, innerInfo, innerEnd, outerInfo, outerEnd, orphanInfo, strayEnd];

        var tree = EventTree.Build(events);

        Assert.Equal(3, tree.Count);
        Assert.Same(rootInfo, Assert.IsType<EventTreeItem>(tree[0]).Event);
        var outer = Assert.IsType<EventTreeSpan>(tree[1]);
        Assert.Equal(("outer", "test", null), (outer.Id, outer.Name, outer.ParentId));
        Assert.Same(outerEnd, outer.End);
        Assert.Equal(2, outer.Children.Count);
        var inner = Assert.IsType<EventTreeSpan>(outer.Children[0]);
        Assert.Equal("outer", inner.ParentId);
        Assert.Same(innerInfo, Assert.IsType<EventTreeItem>(Assert.Single(inner.Children)).Event);
        Assert.Same(outerInfo, Assert.IsType<EventTreeItem>(outer.Children[1]).Event);
        Assert.Same(orphanInfo, Assert.IsType<EventTreeItem>(tree[2]).Event);

        Assert.Equal(events.Take(8), EventTree.Sequence(tree));
        Assert.Equal(6, EventTree.Walk(tree).Count());
    }

    // ---------------------------------------------------------------- jsonable

    [Fact]
    public void jsonable_snapshots_follow_pythons_fallback_rules()
    {
        var state = State();
        var value = Jsonable.FromValue(new Dictionary<string, object?>
        {
            ["state"] = state,
            ["list"] = new List<object?> { 1, 2.5, "s", null, new ChatMessageUser("u"), state },
            ["node"] = Parse("""{"a": [1]}"""),
            ["element"] = JsonDocument.Parse("[true]").RootElement,
            ["float"] = 1.5f,
            ["bytes"] = new byte[] { 1, 2 },
            ["record"] = new ToolCall("id", "fn", new JsonObject { ["a"] = 1 }),
        });

        var obj = Assert.IsType<JsonObject>(value);
        Assert.Equal(["state", "list", "node", "element", "float", "bytes", "record"], obj.Select(pair => pair.Key));
        Assert.Null(obj["state"]);
        var list = Assert.IsType<JsonArray>(obj["list"]);
        Assert.Equal("[1,2.5,\"s\",null", Dump(list)[..15]);
        Assert.Equal("user", list[4]!["role"]!.GetValue<string>());
        Assert.Equal("u", list[4]!["content"]!.GetValue<string>());
        Assert.Null(list[5]);
        Assert.Equal("""{"a":[1]}""", Dump(obj["node"]));
        Assert.Equal("[true]", Dump(obj["element"]));
        Assert.Equal("1.5", Dump(obj["float"]));
        Assert.Equal("\"AQI=\"", Dump(obj["bytes"]));
        Assert.Equal("""{"id":"id","function":"fn","arguments":{"a":1},"type":"function"}""", Dump(obj["record"]));
        Assert.Null(Jsonable.FromValue(null));
        Assert.Equal("{}", Dump(Jsonable.FromStore(new Store())));
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public async Task eval_logs_store_and_state_events_that_replay_to_the_sample_store()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Solver remember = (state, _, _) =>
            {
                state.StoreAs<MyModel>().X = 7;
                state.Store.Set("items", new List<object?> { "a" });
                state.Metadata["seen"] = true;
                return Task.FromResult(state);
            };
            Solver append = (state, _, _) =>
            {
                ((List<object?>)state.Store.Get("items")!).Add("b");
                return Task.FromResult(state);
            };
            var task = new EvalTask
            {
                Name = "store replay",
                Dataset = new MemoryDataset([new Sample("Say hi") { Target = "hi" }]),
                Solver = Solvers.Chain(remember, Solvers.Generate(), append),
            };
            var api = new ScriptedModelApi(ScriptedTurn.Text("hi"));

            var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = logDir, MaxSamples = 1 });

            Assert.Equal(Log.EvalStatus.Success, log.Status);
            var sample = log.Samples![0];
            Assert.Equal(7, sample.Store["MyModel:x"]);
            Assert.Equal(["a", "b"], Assert.IsType<List<object?>>(sample.Store["items"]).Cast<string>());

            var solverSpans = sample.Events.OfType<SpanBeginEvent>().Where(e => e.Type == "solver").ToList();
            // the runner's own "solver" span, then one span per chain step (lambdas are named after their enclosing method)
            Assert.Equal(4, solverSpans.Count);
            Assert.Equal(("solver", "generate"), (solverSpans[0].Name, solverSpans[2].Name));
            Assert.All(solverSpans.Skip(1), span => Assert.Equal(solverSpans[0].Id, span.ParentId));
            var stateEvents = sample.Events.OfType<StateEvent>().ToList();
            Assert.Equal(3, stateEvents.Count);
            Assert.Contains("/metadata/seen", stateEvents[0].GetChanges().Select(c => c.Path));
            Assert.Equal(["/store/items/1"], stateEvents[2].GetChanges().Select(c => c.Path));
            Assert.NotEmpty(sample.Events.OfType<StoreEvent>());

            var replayed = StoreReplay.StoreFromEvents(sample.Events);
            Assert.True(JsonValues.DumpsEquals(Jsonable.FromDictionary(sample.Store), Jsonable.FromStore(replayed)), Dump(Jsonable.FromStore(replayed)));
            Assert.Equal(7, StoreReplay.StoreFromEventsAs<MyModel>(sample.Events).X);
        }
        finally
        {
            if (Directory.Exists(logDir))
            {
                Directory.Delete(logDir, recursive: true);
            }
        }
    }
}
