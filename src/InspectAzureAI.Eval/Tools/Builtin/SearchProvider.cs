namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of <c>SearchProvider</c> (<c>tool/_tools/_web_search/_web_search_provider.py</c>): answers a query with
/// the text or content list to hand the model, or null when nothing relevant was found (the tool then answers
/// "I couldn't find any relevant information on the web.").
/// </summary>
public delegate Task<ToolResult?> SearchProvider(string query, CancellationToken cancellationToken);
