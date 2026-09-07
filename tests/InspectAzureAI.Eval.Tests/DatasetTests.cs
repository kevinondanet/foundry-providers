using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Datasets: JSON/JSONL/CSV loading, record → sample coercion, file resolution and the in-memory dataset operations.</summary>
public class DatasetTests
{
    private static string Fixture(string name) => Path.Combine(FixtureRoot.Value, "datasets", name);

    // fixtures are not copied to the output directory, so walk up from the test assembly to the project's fixtures folder
    private static readonly Lazy<string> FixtureRoot = new(() =>
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("No 'fixtures' directory above " + AppContext.BaseDirectory);
    });

    [Fact]
    public void json_dataset_maps_default_fields_and_resolves_relative_files()
    {
        var path = Fixture("samples.json");

        var dataset = Datasets.Json(path);

        Assert.Equal(3, dataset.Count);
        Assert.Equal("samples", dataset.Name);
        Assert.Equal(Path.GetFullPath(path), dataset.Location);
        Assert.False(dataset.Shuffled);

        var first = dataset[0];
        Assert.Equal(1, first.Id);
        Assert.True(first.Input.IsText);
        Assert.Equal("What is 2+2?", first.Input.Text);
        Assert.Equal("4", first.Target.Text);
        Assert.Equal("python3 -c 'print(4)'", first.Metadata!["check"]);
        Assert.Equal(1, first.Metadata["difficulty"]);
        Assert.Equal(Path.GetFullPath(Fixture(Path.Combine("files", "hello.txt"))), first.Files!["hello.txt"]);
        Assert.Equal("inline text content", first.Files["inline.txt"]);
        Assert.Equal("echo setup", first.Setup);
        Assert.Equal(new SandboxSpec("local"), first.Sandbox);
        Assert.Null(first.Choices);

        var second = dataset[1];
        Assert.Equal("b", second.Id);
        Assert.Equal(new Target(["A", "B"]), second.Target);
        Assert.Equal(["x", "y", "z"], second.Choices);
        Assert.Equal(new SandboxSpec("docker", "files/Dockerfile"), second.Sandbox);
        Assert.Null(second.Metadata);

        var third = dataset[2];
        Assert.Null(third.Id);
        Assert.Equal("42", third.Target.Text);
        Assert.Equal(["p", "q"], third.Choices);
        Assert.Equal("A", third.Files!["a.txt"]);
        Assert.Equal(new SandboxSpec("docker", "img:1"), third.Sandbox);
        Assert.Equal("v", third.Metadata!["k"]);
    }

    [Fact]
    public void jsonl_dataset_reads_message_inputs_and_skips_blank_lines()
    {
        var dataset = Datasets.Json(Fixture("messages.jsonl"));

        Assert.Equal(2, dataset.Count);
        var messages = dataset[0].Input.Messages;
        Assert.NotNull(messages);
        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.Equal("input", m.Source));

        var system = Assert.IsType<ChatMessageSystem>(messages[0]);
        Assert.Equal("Be terse.", system.Text);

        var user = Assert.IsType<ChatMessageUser>(messages[1]);
        Assert.False(user.Content.IsString);
        Assert.Equal("Describe", Assert.IsType<ContentText>(user.Content.Items![0]).Text);
        Assert.Equal(Path.GetFullPath(Fixture(Path.Combine("files", "hello.txt"))), Assert.IsType<ContentImage>(user.Content.Items[1]).Image);

        var assistant = Assert.IsType<ChatMessageAssistant>(messages[2]);
        var call = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("c1", call.Id);
        Assert.Equal("bash", call.Function);
        Assert.Equal("ls", (string?)call.Arguments["cmd"]);

        var tool = Assert.IsType<ChatMessageTool>(messages[3]);
        Assert.Equal("c1", tool.ToolCallId);
        Assert.Equal("bash", tool.Function);
        Assert.Equal(new ToolCallError("unknown", "boom"), tool.Error);

        Assert.Null(dataset[0].Metadata);
        Assert.Equal("plain", dataset[1].Input.Text);
    }

    [Fact]
    public void field_spec_collects_named_metadata_fields()
    {
        var dataset = Datasets.Json(Fixture("messages.jsonl"), new FieldSpec(Metadata: ["level", "extra", "missing"]));

        var metadata = dataset[1].Metadata!;
        Assert.Equal("hard", metadata["level"]);
        Assert.Equal(8, metadata["extra"]);
        Assert.Null(metadata["missing"]);
        Assert.Equal(3, metadata.Count);
    }

    [Fact]
    public void record_to_sample_can_expand_records_and_auto_id_numbers_samples()
    {
        var dataset = Datasets.Json(
            Fixture("messages.jsonl"),
            recordToSample: record => [new Sample("a-" + record["target"]), new Sample("b-" + record["target"])],
            autoId: true);

        Assert.Equal(4, dataset.Count);
        Assert.Equal([1, 2, 3, 4], dataset.Select(s => s.Id).ToArray());
        Assert.Equal(["a-t", "b-t", "a-u", "b-u"], dataset.Select(s => s.Input.Text!).ToArray());
    }

    [Fact]
    public void json_dataset_shuffle_with_seed_is_deterministic_and_limit_slices()
    {
        var first = Datasets.Json(Fixture("samples.json"), shuffle: true, seed: 7);
        var second = Datasets.Json(Fixture("samples.json"), shuffle: true, seed: 7);
        var limited = Datasets.Json(Fixture("samples.json"), limit: 2);
        var oversized = Datasets.Json(Fixture("samples.json"), limit: 10);

        Assert.True(first.Shuffled);
        Assert.Equal(first.Select(s => s.Input.Text), second.Select(s => s.Input.Text));
        Assert.Equal(2, limited.Count);
        Assert.Equal("samples", limited.Name);
        Assert.Equal(3, oversized.Count);
    }

    [Fact]
    public void csv_dataset_parses_quotes_embedded_newlines_and_skips_blank_rows()
    {
        var dataset = Datasets.Csv(Fixture("samples.csv"));

        Assert.Equal(3, dataset.Count);
        Assert.Equal("1", dataset[0].Id);
        Assert.Equal("Say \"hi\"", dataset[0].Input.Text);
        Assert.Equal("hi", dataset[0].Target.Text);
        Assert.Equal("en", dataset[0].Metadata!["lang"]);
        Assert.Equal("multi\nline", dataset[1].Input.Text);
        Assert.Equal("x", dataset[1].Target.Text);
        Assert.Empty(dataset[1].Metadata!);
        Assert.Equal("plain", dataset[2].Input.Text);
    }

    [Fact]
    public void csv_dataset_rejects_ragged_rows_with_python_messages()
    {
        var extra = Fixture("ragged-extra.csv");
        var missing = Fixture("ragged-missing.csv");

        var extraError = Assert.Throws<InvalidDataException>(() => Datasets.Csv(extra));
        var missingError = Assert.Throws<InvalidDataException>(() => Datasets.Csv(missing));

        Assert.Equal($"{extra} line 2 has 3 fields, the header has 2. Unexpected values: ['c'].", extraError.Message);
        Assert.Equal($"{missing} line 2 has 1 field, the header has 3. No value for: target, id.", missingError.Message);
    }

    [Fact]
    public void csv_dataset_supports_explicit_field_names_and_delimiters()
    {
        var path = Path.Combine(Path.GetTempPath(), "inspect-swe-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, "q1;a1\r\nq2;a2\r\n");
        try
        {
            var dataset = Datasets.Csv(path, fieldNames: ["input", "target"], delimiter: ';', name: "custom");

            Assert.Equal("custom", dataset.Name);
            Assert.Equal(["q1", "q2"], dataset.Select(s => s.Input.Text!).ToArray());
            Assert.Equal(["a1", "a2"], dataset.Select(s => s.Target.Text).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void csv_parser_tracks_physical_lines_across_quoted_newlines()
    {
        var records = CsvParser.Parse("a,b\n\"x\ny\",2\r\n\n3,4").ToList();

        Assert.Equal(4, records.Count);
        Assert.Equal(["a", "b"], records[0].Fields);
        Assert.Equal(1, records[0].LineNumber);
        Assert.Equal(["x\ny", "2"], records[1].Fields);
        Assert.Equal(3, records[1].LineNumber);
        Assert.Empty(records[2].Fields);
        Assert.Equal(["3", "4"], records[3].Fields);
        Assert.Equal(5, records[3].LineNumber);
    }

    [Fact]
    public void memory_dataset_filter_sort_slice_and_shuffle()
    {
        var samples = new[]
        {
            new Sample("ccc") { Id = 1 },
            new Sample("a") { Id = 2 },
            new Sample("bb") { Id = 3 },
            new Sample("dd") { Id = 4 },
        };
        var dataset = new MemoryDataset(samples, name: "mem", location: "/tmp/mem.json");

        var filtered = dataset.Filter(s => s.Input.Text!.Length > 1, "long");
        Assert.Equal("long", filtered.Name);
        Assert.Equal("/tmp/mem.json", filtered.Location);
        Assert.Equal([1, 3, 4], filtered.Select(s => s.Id).ToArray());
        Assert.Equal("mem", dataset.Filter(_ => true).Name);

        dataset.Sort();
        Assert.Equal([2, 3, 4, 1], dataset.Select(s => s.Id).ToArray());
        dataset.Sort(reverse: true);
        Assert.Equal([1, 3, 4, 2], dataset.Select(s => s.Id).ToArray());
        dataset.Sort(key: s => (int)s.Id!);
        Assert.Equal([1, 2, 3, 4], dataset.Select(s => s.Id).ToArray());

        var slice = dataset.Slice(1..3);
        Assert.Equal([2, 3], slice.Select(s => s.Id).ToArray());
        Assert.Equal("mem", slice.Name);
        Assert.Equal(4, dataset.Slice(..99).Count);

        var other = new MemoryDataset(samples);
        dataset.Shuffle(seed: 42);
        other.Shuffle(seed: 42);
        Assert.True(dataset.Shuffled);
        Assert.Equal(dataset.Select(s => s.Id), other.Select(s => s.Id));
        Assert.Equal([1, 2, 3, 4], dataset.Select(s => s.Id).Order());
    }

    [Fact]
    public void sample_input_length_matches_python_sample_input_len()
    {
        var text = new Sample("hello");
        var messages = new Sample(new ChatMessage[] { new ChatMessageUser("ab"), new ChatMessageAssistant("cde") });

        Assert.Equal(5, text.InputLength);
        Assert.Equal(5, messages.InputLength);
    }

    [Fact]
    public void read_helpers_reject_invalid_records_like_python()
    {
        Assert.Equal("No input in dataset", Assert.Throws<InvalidDataException>(() => SampleRecords.ReadInput(null)).Message);
        Assert.Equal("No input in dataset", Assert.Throws<InvalidDataException>(() => SampleRecords.ReadInput(JsonValue.Create(""))).Message);
        Assert.Equal(
            "Invalid 'sandbox' value: '[\"a\",\"b\",\"c\"]'. Sandbox must be string or 2-item list",
            Assert.Throws<InvalidDataException>(() => SampleRecords.ReadSandbox(new JsonArray("a", "b", "c"))).Message);
        Assert.StartsWith("Unexpected type for 'sandbox' field:", Assert.Throws<InvalidDataException>(() => SampleRecords.ReadSandbox(JsonValue.Create(3))).Message);
        Assert.StartsWith("Unexpected type for 'files' field:", Assert.Throws<InvalidDataException>(() => SampleRecords.ReadFiles(new JsonObject { ["a"] = 1 })).Message);
        Assert.Equal(
            "content not specified for chat input in dataset",
            Assert.Throws<InvalidDataException>(() => SampleRecords.ReadMessages([new JsonObject { ["role"] = "user" }])).Message);
        Assert.Equal(
            "role not specified for chat input in dataset",
            Assert.Throws<InvalidDataException>(() => SampleRecords.ReadMessages([new JsonObject { ["role"] = "bot", ["content"] = "x" }])).Message);
        Assert.ThrowsAny<JsonException>(() => Datasets.Json(Fixture("ragged-extra.csv")));
    }

    [Fact]
    public void read_helpers_coerce_scalars_like_python()
    {
        Assert.Equal(Target.Empty, SampleRecords.ReadTarget(null));
        Assert.Equal("True", SampleRecords.ReadTarget(JsonValue.Create(true)).Text);
        Assert.Equal("1.5", SampleRecords.ReadTarget(JsonValue.Create(1.5)).Text);
        Assert.Equal(["1", "x"], SampleRecords.ReadTarget(new JsonArray(1, "x"))!.Values);
        Assert.Equal(["a", "b", "c"], SampleRecords.ReadChoices(JsonValue.Create("a b  c")));
        Assert.Equal(["7"], SampleRecords.ReadChoices(JsonValue.Create(7)));
        Assert.Null(SampleRecords.ReadChoices(null));
        Assert.Equal(new SandboxSpec("docker"), SampleRecords.ReadSandbox(JsonValue.Create("docker")));
        Assert.Equal(new SandboxSpec("docker", "compose.yaml"), SampleRecords.ReadSandbox(new JsonArray("docker", "compose.yaml")));
        Assert.Equal("B", SampleRecords.ReadFiles(JsonValue.Create("{\"b\": \"B\"}"))!["b"]);
    }
}
