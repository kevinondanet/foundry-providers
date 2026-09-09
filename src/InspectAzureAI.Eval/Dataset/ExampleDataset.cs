using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>
/// Port of <c>dataset/_sources/example.py</c> <c>example_dataset</c>. The files of <c>dataset/_examples/</c> are
/// copied to <c>Dataset/Examples/</c> and embedded in this assembly as
/// <c>InspectAzureAI.Eval.Examples.{name}.jsonl</c> (see the csproj <c>EmbeddedResource</c> item).
/// </summary>
public static partial class Datasets
{
    /// <summary>Logical resource name prefix of the bundled example datasets (set in the csproj <c>EmbeddedResource</c> item).</summary>
    internal const string ExamplesResourcePrefix = "InspectAzureAI.Eval.Examples.";

    /// <summary>
    /// Port of <c>example_dataset</c>: reads a dataset bundled with the package — primarily for runnable example
    /// snippets that don't need an external dataset. <paramref name="name"/> is one of <c>security_guide</c>,
    /// <c>theory_of_mind</c>, <c>popularity</c>, <c>biology_qa</c> or <c>bias_detection</c> (see
    /// <see cref="ExampleNames()"/>); an unknown name is an <see cref="ArgumentException"/> (Python's
    /// <c>ValueError</c>) whose message lists the available datasets. As in Python a <c>{name}.jsonl</c> file is
    /// tried first, then <c>{name}.csv</c>, and the result is a <see cref="MemoryDataset"/> named
    /// <paramref name="name"/> at location <c>example://{name}</c>. The mapping arguments are those of
    /// <see cref="Json"/>: <paramref name="recordToSample"/> wins over <paramref name="fields"/>, and a null
    /// <paramref name="fields"/> expects records already in sample form (<c>input</c>/<c>target</c> fields).
    /// Deviation: the files are embedded resources rather than package files, so relative file references are not
    /// resolved against a directory (the bundled sets carry none); <paramref name="shuffle"/> and
    /// <paramref name="seed"/> are conveniences Python's <c>example_dataset</c> does not take.
    /// </summary>
    public static IDataset Example(
        string name,
        FieldSpec? fields = null,
        RecordToSample? recordToSample = null,
        bool shuffle = false,
        int? seed = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var assembly = typeof(Datasets).Assembly;
        IEnumerable<JsonObject>? records = null;
        if (ReadExampleResource(assembly, name + ".jsonl") is { } jsonl)
        {
            records = ReadJsonLines(jsonl);
        }
        else if (ReadExampleResource(assembly, name + ".csv") is { } csv)
        {
            records = ReadCsv($"example://{name}.csv", csv, fieldNames: null, delimiter: ',');
        }

        if (records is null)
        {
            var available = string.Join(", ", ExampleNames(assembly).Select(n => $"'{n}'"));
            throw new ArgumentException($"Sample dataset {name} not found. Available datasets: [{available}]");
        }

        var mapper = recordToSample ?? SampleRecords.Mapper(fields ?? new FieldSpec());
        IDataset dataset = new MemoryDataset(SampleRecords.ToSamples(records, mapper, autoId: false), name, $"example://{name}");
        if (shuffle)
        {
            dataset.Shuffle(seed);
        }

        return dataset;
    }

    /// <summary>The bundled example dataset names (Python: the file stems of <c>dataset/_examples</c>), in ordinal order.</summary>
    public static IReadOnlyList<string> ExampleNames() => ExampleNames(typeof(Datasets).Assembly);

    private static IReadOnlyList<string> ExampleNames(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(ExamplesResourcePrefix, StringComparison.Ordinal))
            .Select(resource => Path.GetFileNameWithoutExtension(resource[ExamplesResourcePrefix.Length..]))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? ReadExampleResource(Assembly assembly, string file)
    {
        using var stream = assembly.GetManifestResourceStream(ExamplesResourcePrefix + file);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
