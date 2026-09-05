using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>Port of <c>tool/_mcp/_config.py</c> <c>MCPServerConfig</c>: configuration for an MCP server.</summary>
/// <param name="Type">Server type: <c>stdio</c>, <c>http</c> or <c>sse</c>.</param>
/// <param name="Name">Human readable server name.</param>
public abstract record McpServerConfig(string Type, string Name)
{
    /// <summary>Tools (names or globs) to make available from the server; null is Python's <c>"all"</c>.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Port of <c>model_dump()</c>: <c>type</c>, <c>name</c>, <c>tools</c> and then the subtype's fields, in pydantic's order.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["type"] = Type,
            ["name"] = Name,
            ["tools"] = Tools is null ? JsonValue.Create("all") : StringArray(Tools),
        };
        AddFields(obj);
        return obj;
    }

    /// <summary>Appends the subtype's own fields to the dump.</summary>
    protected abstract void AddFields(JsonObject obj);

    /// <summary>A JSON array of strings.</summary>
    protected static JsonArray StringArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>A JSON object of string values, or null.</summary>
    protected static JsonObject? StringMap(IReadOnlyDictionary<string, string>? values) =>
        values is null ? null : new JsonObject(values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
}

/// <summary>Port of <c>MCPServerConfigStdio</c>: configuration for MCP servers with a stdio interface.</summary>
/// <param name="Name">Human readable server name.</param>
/// <param name="Command">The executable to run to start the server.</param>
public sealed record McpServerConfigStdio(string Name, string Command) : McpServerConfig("stdio", Name)
{
    /// <summary>Command line arguments to pass to the executable.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The working directory to use when spawning the process.</summary>
    public string? Cwd { get; init; }

    /// <summary>Environment variables added to the platform default set (HOME, LOGNAME, PATH, SHELL, TERM, USER on POSIX).</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <inheritdoc />
    protected override void AddFields(JsonObject obj)
    {
        obj["command"] = Command;
        obj["args"] = StringArray(Args);
        obj["cwd"] = Cwd;
        obj["env"] = StringMap(Env);
    }
}

/// <summary>Port of <c>MCPServerConfigHTTP</c>: configuration for MCP servers with an HTTP (<c>http</c> or <c>sse</c>) interface.</summary>
public sealed record McpServerConfigHttp : McpServerConfig
{
    /// <summary>Creates the config; <paramref name="type"/> must be <c>http</c> or <c>sse</c>.</summary>
    public McpServerConfigHttp(string type, string name, string url, IReadOnlyDictionary<string, string>? headers = null)
        : base(type, name)
    {
        if (type is not ("http" or "sse"))
        {
            throw new ArgumentException($"MCP HTTP server type must be 'http' or 'sse', not '{type}'.", nameof(type));
        }

        ArgumentException.ThrowIfNullOrEmpty(url);
        Url = url;
        Headers = headers;
    }

    /// <summary>URL for the remote server.</summary>
    public string Url { get; init; }

    /// <summary>Headers for the remote server.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Port of <c>authorization_token</c>: the <c>Authorization</c> header with a <c>Bearer </c> prefix (any case) removed, or null.</summary>
    public string? AuthorizationToken
    {
        get
        {
            if (Headers is null || !Headers.TryGetValue("Authorization", out var authorization))
            {
                return null;
            }

            return authorization.StartsWith("BEARER ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : authorization;
        }
    }

    /// <inheritdoc />
    protected override void AddFields(JsonObject obj)
    {
        obj["url"] = Url;
        obj["headers"] = StringMap(Headers);
    }
}
