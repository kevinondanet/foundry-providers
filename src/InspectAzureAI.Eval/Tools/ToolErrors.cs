namespace InspectAzureAI.Eval.Tools;

/// <summary>Port of <c>tool/_tool.py</c> <c>ToolError</c>: reported to the model as a tool error; the sample continues.</summary>
public class ToolError(string message) : Exception(message);

/// <summary>Port of <c>tool/_tool.py</c> <c>ToolParsingError</c>: the call's arguments could not be interpreted.</summary>
public sealed class ToolParsingError(string message) : ToolError(message);
