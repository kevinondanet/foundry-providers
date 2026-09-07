using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>
/// Port of <c>dataset/_dataset.py</c> <c>FieldSpec</c>: which record fields feed each <see cref="Sample"/>
/// property. <see cref="Metadata"/> names the record fields collected as metadata; when null, a record's
/// own <c>metadata</c> field (object or JSON string) is used, as in Python.
/// </summary>
public sealed record FieldSpec(
    string Input = "input",
    string Target = "target",
    string Choices = "choices",
    string Id = "id",
    IReadOnlyList<string>? Metadata = null,
    string Sandbox = "sandbox",
    string Files = "files",
    string Setup = "setup");

/// <summary>Port of the <c>RecordToSample</c> callable: maps one record to one or more samples.</summary>
public delegate IEnumerable<Sample> RecordToSample(JsonObject record);
