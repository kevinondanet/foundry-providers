using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Util;

/// <summary>Small helpers reproducing Python semantics the provider depends on.</summary>
public static class PythonSemantics
{
    /// <summary>
    /// Python truthiness (<c>not not value</c>): null, false, zero, empty strings/collections are
    /// falsy; everything else (including the string <c>"false"</c>) is truthy.
    /// </summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        float f => f != 0,
        decimal m => m != 0,
        JsonValue jv => jv.TryGetValue<bool>(out var jb) ? jb : Truthy(jv.GetValue<object>()),
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.True => true,
            JsonValueKind.String => je.GetString()!.Length > 0,
            JsonValueKind.Number => je.GetDouble() != 0,
            JsonValueKind.Array => je.GetArrayLength() > 0,
            JsonValueKind.Object => je.EnumerateObject().Any(),
            _ => true,
        },
        JsonArray ja => ja.Count > 0,
        JsonObject jo => jo.Count > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>Python's <c>string.whitespace</c> characters.</summary>
    public const string Whitespace = " \t\n\r\x0b\x0c";

    /// <summary>Python <c>str.strip()</c> (strips Unicode whitespace).</summary>
    public static string Strip(string s) => s.Trim();
}
