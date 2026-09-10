using System.Text.Json.Nodes;
namespace InspectAzureAI.Provider.Core;

/// <summary>An HTTP failure retaining the response needed for retry and protocol-specific classification.</summary>
public sealed class ProviderHttpException : Exception
{
    public ProviderHttpException(int status, IReadOnlyDictionary<string, string> headers, string body)
        : base(MessageFor(status, body))
    {
        Status = status;
        Headers = headers;
        Body = body;
        try { Error = JsonNode.Parse(body)?["error"]?.DeepClone(); } catch (System.Text.Json.JsonException) { }
    }
    public int Status { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string Body { get; }
    public JsonNode? Error { get; }
    private static string MessageFor(int status, string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.ToString() ?? $"Provider returned HTTP {status}."; }
        catch (System.Text.Json.JsonException) { return $"Provider returned HTTP {status}."; }
    }
}
