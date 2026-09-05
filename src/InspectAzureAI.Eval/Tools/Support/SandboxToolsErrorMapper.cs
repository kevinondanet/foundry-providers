using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>
/// Port of <c>tool/_sandbox_tools_utils/_error_mapper.py</c> <c>SandboxToolsErrorMapper</c>: maps the
/// sandbox tools' JSON-RPC error codes to tool-layer exceptions so they are fed back to the model rather
/// than ending the sample. -32099 is a <c>ToolException</c> raised inside the container (a
/// <see cref="ToolError"/>); -32098 is an unexpected exception in the container (an Inspect coding
/// error, so an <see cref="InvalidOperationException"/> that fails the sample); invalid params (-32602)
/// is a <see cref="ToolParsingError"/>; an internal error (-32603) is a <see cref="ToolError"/>.
/// </summary>
public sealed class SandboxToolsErrorMapper : JsonRpcErrorMapper
{
    public const int ToolExceptionCode = -32099;

    public const int UnexpectedExceptionCode = -32098;

    public static readonly SandboxToolsErrorMapper Instance = new();

    public override Exception ServerError(int code, string message, string method, JsonNode? parameters) => code switch
    {
        ToolExceptionCode => new ToolError(message),
        UnexpectedExceptionCode => new InvalidOperationException(message),
        _ => new InvalidOperationException(message),
    };

    public override Exception InvalidParams(string message, string method, JsonNode? parameters) => new ToolParsingError(message);

    public override Exception InternalError(string message, string method, JsonNode? parameters) => new ToolError(message);
}
