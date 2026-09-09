using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>
/// Ports of <c>dataset/_sources/json.py</c> <c>json_dataset</c> and <c>dataset/_sources/csv.py</c>
/// <c>csv_dataset</c> for local files. Both follow the Python order: map records → resolve relative file
/// references → shuffle → limit. Only local paths are supported (no S3 / HTTP).
/// </summary>
public static partial class Datasets
{
    /// <summary>
    /// Port of <c>json_dataset</c>: a <c>.jsonl</c> file (one object per line) or a <c>.json</c> file holding an
    /// array of objects (or a single object). <paramref name="recordToSample"/> wins over <paramref name="fields"/>.
    /// </summary>
    public static IDataset Json(
        string path,
        FieldSpec? fields = null,
        RecordToSample? recordToSample = null,
        string? name = null,
        bool shuffle = false,
        int? seed = null,
        int? limit = null,
        bool autoId = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var records = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? ReadJsonLines(text) : ReadJson(text);
        return Build(path, records, fields, recordToSample, name, shuffle, seed, limit, autoId);
    }

    /// <summary>
    /// Port of <c>csv_dataset</c>: the first row names the columns unless <paramref name="fieldNames"/> is given;
    /// every value is a string (as with <c>csv.DictReader</c>); ragged rows are rejected and all-blank rows skipped.
    /// </summary>
    public static IDataset Csv(
        string path,
        FieldSpec? fields = null,
        RecordToSample? recordToSample = null,
        string? name = null,
        bool shuffle = false,
        int? seed = null,
        int? limit = null,
        bool autoId = false,
        IReadOnlyList<string>? fieldNames = null,
        char delimiter = ',')
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var records = ReadCsv(path, text, fieldNames, delimiter);
        return Build(path, records, fields, recordToSample, name, shuffle, seed, limit, autoId);
    }

    private static IDataset Build(
        string path,
        IEnumerable<JsonObject> records,
        FieldSpec? fields,
        RecordToSample? recordToSample,
        string? name,
        bool shuffle,
        int? seed,
        int? limit,
        bool autoId)
    {
        var mapper = recordToSample ?? SampleRecords.Mapper(fields ?? new FieldSpec());
        var location = Path.GetFullPath(path);
        var samples = SampleRecords.ResolveFiles(SampleRecords.ToSamples(records, mapper, autoId), location);
        IDataset dataset = new MemoryDataset(samples, name ?? Path.GetFileNameWithoutExtension(path), location);
        if (shuffle)
        {
            dataset.Shuffle(seed);
        }

        return limit is { } count ? dataset.Slice(..count) : dataset;
    }

    private static IEnumerable<JsonObject> ReadJson(string text)
    {
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return node switch
        {
            JsonArray array => array.Select((item, index) => item as JsonObject ?? throw new InvalidDataException($"Dataset record {index} is not an object.")),
            JsonObject obj => [obj],
            _ => throw new InvalidDataException($"Could not read json into a supported type, found: {node?.GetValueKind().ToString() ?? "null"}"),
        };
    }

    private static IEnumerable<JsonObject> ReadJsonLines(string text)
    {
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException($"Line {lineNumber} is not a JSON object.");
        }
    }

    private static List<JsonObject> ReadCsv(string path, string text, IReadOnlyList<string>? fieldNames, char delimiter)
    {
        var records = new List<JsonObject>();
        string[]? header = fieldNames?.ToArray();
        foreach (var record in CsvParser.Parse(text, delimiter))
        {
            if (header is null)
            {
                header = record.Fields.ToArray();
                continue;
            }

            // DictReader skips blank lines (empty records) entirely
            if (record.Fields.Count == 0)
            {
                continue;
            }

            if (record.Fields.Count != header.Length)
            {
                throw RaggedRow(path, header, record);
            }

            if (record.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var obj = new JsonObject();
            for (var i = 0; i < header.Length; i++)
            {
                obj[header[i]] = record.Fields[i];
            }

            records.Add(obj);
        }

        return records;
    }

    /// <summary>Port of <c>_raise_ragged_row</c>: the same messages as Python, naming the physical line.</summary>
    private static InvalidDataException RaggedRow(string path, string[] header, CsvRecord record)
    {
        var found = record.Fields.Count;
        var plural = found != 1 ? "s" : "";
        if (found > header.Length)
        {
            var extras = string.Join(", ", record.Fields.Skip(header.Length).Select(value => $"'{value}'"));
            return new InvalidDataException($"{path} line {record.LineNumber} has {found} field{plural}, the header has {header.Length}. Unexpected values: [{extras}].");
        }

        var missing = string.Join(", ", header.Skip(found));
        return new InvalidDataException($"{path} line {record.LineNumber} has {found} field{plural}, the header has {header.Length}. No value for: {missing}.");
    }
}
