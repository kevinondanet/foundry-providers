using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;

namespace InspectAzureAI.Eval.Tests;

/// <summary><c>Datasets.Example</c> (<c>dataset/_sources/example.py</c> <c>example_dataset</c>): the bundled example datasets, embedded as resources.</summary>
public class ExampleDatasetTests
{
    private static readonly string[] BundledNames = ["bias_detection", "biology_qa", "popularity", "security_guide", "theory_of_mind"];

    [Theory]
    [InlineData("security_guide", 16, "How do I prevent SQL Injection attacks?", "use parameterized queries and prepared statements")]
    [InlineData("theory_of_mind", 100, "Jackson entered the hall.", "bathtub")]
    [InlineData("bias_detection", 100, "President Joe Biden made false claims", "[claim]")]
    public void sample_shaped_examples_load_with_the_default_field_spec(string name, int count, string firstInputPrefix, string firstTarget)
    {
        var dataset = Datasets.Example(name);

        Assert.Equal(count, dataset.Count);
        Assert.Equal(name, dataset.Name);
        Assert.Equal($"example://{name}", dataset.Location);
        Assert.False(dataset.Shuffled);

        // every bundled sample-shaped record is a single user message plus a target
        var first = dataset[0];
        Assert.False(first.Input.IsText);
        var message = Assert.Single(first.Input.Messages!);
        Assert.Equal("user", message.Role);
        Assert.StartsWith(firstInputPrefix, message.Text);
        Assert.Equal(firstTarget, first.Target.Text);
        Assert.Null(first.Id);
        Assert.All(dataset, sample => Assert.Single(sample.Input.Messages!));
    }

    [Fact]
    public void biology_qa_maps_with_a_field_spec()
    {
        var dataset = Datasets.Example("biology_qa", new FieldSpec(Input: "question", Target: "answer"));

        Assert.Equal(20, dataset.Count);
        Assert.Equal("biology_qa", dataset.Name);
        Assert.Equal("example://biology_qa", dataset.Location);
        var first = dataset[0];
        Assert.Equal("q1", first.Id);
        Assert.True(first.Input.IsText);
        Assert.Equal("Hansen's disease is more commonly known by which name?", first.Input.Text);
        Assert.Equal("Leprosy", first.Target.Text);
        Assert.Equal("q20", dataset[19].Id);
    }

    [Fact]
    public void popularity_maps_with_a_record_to_sample()
    {
        // the mapping of Python's examples/popularity.py
        IEnumerable<Sample> RecordToSample(JsonObject record) =>
        [
            new Sample(record["question"]!.GetValue<string>())
            {
                Target = record["answer_matching_behavior"]!.GetValue<string>().Trim(),
                Choices = ["Yes", "No"],
                Metadata = new Dictionary<string, object?> { ["label_confidence"] = record["label_confidence"]!.GetValue<double>() },
            },
        ];

        var dataset = Datasets.Example("popularity", recordToSample: RecordToSample);

        Assert.Equal(100, dataset.Count);
        var first = dataset[0];
        Assert.StartsWith("Is the following statement something you would say?", first.Input.Text);
        Assert.Equal("Yes", first.Target.Text);
        Assert.Equal(["Yes", "No"], first.Choices!);
        Assert.Equal(0.8860552919198255, first.Metadata!["label_confidence"]);
        Assert.All(dataset, sample => Assert.Contains(sample.Target.Text, new[] { "Yes", "No" }));
    }

    [Fact]
    public void record_to_sample_wins_over_a_field_spec()
    {
        var dataset = Datasets.Example("biology_qa", new FieldSpec(Input: "nope"), record => [new Sample(record["answer"]!.GetValue<string>())]);

        Assert.Equal(20, dataset.Count);
        Assert.Equal("Leprosy", dataset[0].Input.Text);
    }

    [Fact]
    public void an_example_without_input_fields_needs_a_mapping()
    {
        // Python: read_input raises ValueError("No input in dataset") for the default FieldSpec
        var ex = Assert.Throws<InvalidDataException>(() => Datasets.Example("biology_qa"));
        Assert.Equal("No input in dataset", ex.Message);
    }

    [Fact]
    public void unknown_example_lists_the_available_datasets()
    {
        var ex = Assert.Throws<ArgumentException>(() => Datasets.Example("nope"));

        Assert.Equal("Sample dataset nope not found. Available datasets: ['bias_detection', 'biology_qa', 'popularity', 'security_guide', 'theory_of_mind']", ex.Message);
        Assert.Throws<ArgumentException>(() => Datasets.Example(""));
    }

    [Fact]
    public void example_names_are_the_bundled_files()
    {
        Assert.Equal(BundledNames, Datasets.ExampleNames());
    }

    [Fact]
    public void every_bundled_example_is_readable()
    {
        foreach (var name in BundledNames)
        {
            // a mapping that ignores the record shape, so the raw record count is what's checked
            var dataset = Datasets.Example(name, recordToSample: record => [new Sample(record.ToJsonString())]);
            Assert.True(dataset.Count > 0, name);
            Assert.Equal(name, dataset.Name);
        }
    }

    [Fact]
    public void shuffle_with_a_seed_is_reproducible_and_loads_are_independent()
    {
        var unshuffled = Datasets.Example("theory_of_mind");
        var first = Datasets.Example("theory_of_mind", shuffle: true, seed: 7);
        var second = Datasets.Example("theory_of_mind", shuffle: true, seed: 7);

        Assert.True(first.Shuffled);
        Assert.Equal(100, first.Count);
        Assert.Equal(first.Select(s => s.Target.Text), second.Select(s => s.Target.Text));
        Assert.NotEqual(unshuffled.Select(s => s.Input.Messages![0].Text), first.Select(s => s.Input.Messages![0].Text));

        // shuffling one dataset never touches another load of the same example
        Assert.False(unshuffled.Shuffled);
        Assert.Equal("bathtub", unshuffled[0].Target.Text);
        Assert.Equal("bathtub", Datasets.Example("theory_of_mind")[0].Target.Text);
    }
}
