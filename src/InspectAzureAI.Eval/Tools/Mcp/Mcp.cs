using System.Text;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Util;
using ModelContextProtocol.Client;

namespace InspectAzureAI.Eval.Tools.Mcp;

/// <summary>Where an HTTP MCP server's tool calls execute (port of the <c>execution</c> literal).</summary>
public enum McpExecution
{
    /// <summary>Within this process.</summary>
    Local,

    /// <summary>By the model provider (only OpenAI and Anthropic support this in Python; the Azure AI provider does not).</summary>
    Remote,
}

/// <summary>
/// Port of <c>tool/_mcp/server.py</c> and <c>tools.py</c>: the server factories (<see cref="McpServerStdio"/>,
/// <see cref="McpServerSse"/>, <see cref="McpServerHttp"/>, <see cref="McpServerSandbox"/>) and
/// <see cref="McpTools"/> for selecting a subset of a server's tools.
/// </summary>
public static class Mcp
{
    /// <summary>Default HTTP operation timeout (Python's <c>timeout=5</c>).</summary>
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Default wait for a new SSE event before disconnecting (Python's <c>sse_read_timeout=300</c>).</summary>
    public static readonly TimeSpan DefaultSseReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Port of <c>mcp_server_stdio</c>: stdio interface to an MCP server running locally. The server's stderr
    /// lines are logged through <see cref="ProviderLogger"/>.
    /// </summary>
    /// <param name="command">The executable to run to start the server.</param>
    /// <param name="args">Command line arguments to pass to the executable.</param>
    /// <param name="name">Human readable name for the server (defaults to the command line).</param>
    /// <param name="cwd">The working directory to use when spawning the process.</param>
    /// <param name="env">Environment variables added to the platform default set (HOME, LOGNAME, PATH, SHELL, TERM and USER on POSIX).</param>
    public static McpServer McpServerStdio(string command, IReadOnlyList<string>? args = null, string? name = null, string? cwd = null, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        var serverName = name ?? CommandLine(command, args);
        return new McpServerLocal(() => StdioTransport(serverName, command, args, cwd, env), serverName, events: true);
    }

    /// <summary>
    /// Port of <c>mcp_server_sse</c>: SSE interface to an MCP server at a URL (deprecated by MCP in favour of
    /// <see cref="McpServerHttp"/>).
    /// </summary>
    /// <param name="url">URL of the remote server.</param>
    /// <param name="name">Human readable name for the server (defaults to <paramref name="url"/>).</param>
    /// <param name="execution">Where tool calls execute.</param>
    /// <param name="authorization">OAuth Bearer token sent as the <c>Authorization</c> header.</param>
    /// <param name="headers">Headers to send the server (typically authorization is included here).</param>
    /// <param name="timeout">Timeout for HTTP operations (connecting); default 5 seconds.</param>
    /// <param name="sseReadTimeout">How long to wait for a new event before disconnecting; default 5 minutes.</param>
    public static McpServer McpServerSse(
        string url,
        string? name = null,
        McpExecution execution = McpExecution.Local,
        string? authorization = null,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        TimeSpan? sseReadTimeout = null) =>
        HttpServer("sse", HttpTransportMode.Sse, url, name, execution, authorization, headers, timeout, sseReadTimeout);

    /// <summary>Port of <c>mcp_server_http</c>: streamable HTTP interface to an MCP server at a URL.</summary>
    /// <param name="url">URL of the remote server.</param>
    /// <param name="name">Human readable name for the server (defaults to <paramref name="url"/>).</param>
    /// <param name="execution">Where tool calls execute.</param>
    /// <param name="authorization">OAuth Bearer token sent as the <c>Authorization</c> header.</param>
    /// <param name="headers">Headers to send the server (typically authorization is included here).</param>
    /// <param name="timeout">Timeout for HTTP operations (connecting); default 5 seconds.</param>
    /// <param name="sseReadTimeout">How long to wait for a new event before disconnecting; default 5 minutes.</param>
    public static McpServer McpServerHttp(
        string url,
        string? name = null,
        McpExecution execution = McpExecution.Local,
        string? authorization = null,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        TimeSpan? sseReadTimeout = null) =>
        HttpServer("http", HttpTransportMode.StreamableHttp, url, name, execution, authorization, headers, timeout, sseReadTimeout);

    /// <summary>
    /// Port of <c>mcp_server_sandbox</c>: interface to an MCP server running inside the sample's sandbox through
    /// the injected sandbox tools CLI (<see cref="McpSandboxExecRpc.DefaultCli"/>). The sandbox is resolved from
    /// the ambient <see cref="SampleContext"/> when the connection opens.
    /// </summary>
    /// <param name="command">The executable to run to start the server.</param>
    /// <param name="args">Command line arguments to pass to the executable.</param>
    /// <param name="name">Human readable name for the server (defaults to the command line).</param>
    /// <param name="cwd">The working directory to use when spawning the process.</param>
    /// <param name="env">Environment variables added to the platform default set when spawning the process.</param>
    /// <param name="sandbox">The sandbox to use when spawning the process (default sandbox when null).</param>
    /// <param name="timeout">Timeout for each command; default <see cref="McpSandboxClientTransport.DefaultSandboxTimeout"/>.</param>
    public static McpServer McpServerSandbox(
        string command,
        IReadOnlyList<string>? args = null,
        string? name = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? sandbox = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        // Normalize the default once so the carrier timeout and the host-side call timeout share one value; a
        // null host timeout would leave a lost transport response deadlocking the call.
        var effectiveTimeout = timeout ?? McpSandboxClientTransport.DefaultSandboxTimeout;
        var serverName = name ?? CommandLine(command, args);
        var server = new McpStdioServerParameters(command, args, cwd, env);
        return new McpServerLocal(
            () => new McpSandboxClientTransport(
                server,
                _ => Task.FromResult<IMcpSandboxRpc>(new McpSandboxExecRpc(SampleContext.Require().Sandbox(sandbox))),
                effectiveTimeout,
                serverName),
            serverName,
            events: false,
            effectiveTimeout);
    }

    /// <summary>Port of <c>mcp_tools</c>: a source for the server's tools whose names match <paramref name="tools"/> (names or globs; null is all).</summary>
    public static IToolSource McpTools(McpServer server, IReadOnlyList<string>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server switch
        {
            McpServerLocal local => new McpToolSourceLocal(local, tools),
            McpServerRemote remote => new McpServerRemote(remote.Config with { Tools = tools }),
            _ => throw new ArgumentException($"Unexpected MCPServer type: {server.GetType().Name}", nameof(server)),
        };
    }

    /// <summary>Port of <c>_resolve_headers</c>: merges an <c>Authorization: Bearer</c> header into the headers.</summary>
    internal static IReadOnlyDictionary<string, string>? ResolveHeaders(string? authorization, IReadOnlyDictionary<string, string>? headers)
    {
        if (authorization is null && headers is null)
        {
            return null;
        }

        var result = headers is null ? new Dictionary<string, string>(StringComparer.Ordinal) : headers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (authorization is not null)
        {
            result["Authorization"] = $"Bearer {authorization}";
        }

        return result;
    }

    /// <summary>Port of mcp's default environment plus the caller's additions.</summary>
    internal static Dictionary<string, string?> StdioEnvironment(IReadOnlyDictionary<string, string>? env)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return environment;
    }

    internal static StdioClientTransport StdioTransport(string name, string command, IReadOnlyList<string>? args, string? cwd, IReadOnlyDictionary<string, string>? env)
    {
        var options = new StdioClientTransportOptions
        {
            Command = command,
            Arguments = args?.ToList() ?? [],
            Name = name,
            WorkingDirectory = cwd,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioEnvironment(env),
            StandardErrorLines = line =>
            {
                var stripped = line.TrimEnd('\r', '\n');
                if (stripped.Length > 0)
                {
                    ProviderLogger.Info($"[mcp:{name}] {stripped}");
                }
            },
        };
        return new StdioClientTransport(options);
    }

    internal static HttpClientTransport HttpTransport(string name, HttpTransportMode mode, string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, TimeSpan sseReadTimeout)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = mode,
            Name = name,
            ConnectionTimeout = timeout,
            AdditionalHeaders = headers?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
        // The SDK has no idle-read timeout for the event stream; the HttpClient timeout is the closest bound.
        var httpClient = new HttpClient { Timeout = sseReadTimeout };
        return new HttpClientTransport(options, httpClient, ownsHttpClient: true);
    }

    private static McpServer HttpServer(
        string type,
        HttpTransportMode mode,
        string url,
        string? name,
        McpExecution execution,
        string? authorization,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan? timeout,
        TimeSpan? sseReadTimeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        var serverName = name ?? url;
        var resolvedHeaders = ResolveHeaders(authorization, headers);
        return execution switch
        {
            McpExecution.Local => new McpServerLocal(
                () => HttpTransport(serverName, mode, url, resolvedHeaders, timeout ?? DefaultHttpTimeout, sseReadTimeout ?? DefaultSseReadTimeout),
                serverName,
                events: true),
            McpExecution.Remote => new McpServerRemote(new McpServerConfigHttp(type, serverName, url, resolvedHeaders)),
            _ => throw new ArgumentOutOfRangeException(nameof(execution), $"Unexpected execution type: {execution}"),
        };
    }

    private static string CommandLine(string command, IReadOnlyList<string>? args) => string.Join(" ", new[] { command }.Concat(args ?? []));
}

/// <summary>
/// Port of <c>tools.py</c> <c>MCPToolSourceLocal</c>: filters a local server's tools by name globs. Never caches:
/// every call re-resolves through the server so the tools bind to the caller's own session (the server's session
/// caches the raw list, so this costs no extra round trip).
/// </summary>
public sealed class McpToolSourceLocal : IToolSource
{
    /// <summary>Creates the source.</summary>
    /// <param name="server">The server to draw tools from.</param>
    /// <param name="tools">Tool names or globs to include; null is all.</param>
    public McpToolSourceLocal(McpServer server, IReadOnlyList<string>? tools)
    {
        ArgumentNullException.ThrowIfNull(server);
        Server = server;
        Tools = tools;
    }

    /// <summary>The server tools are drawn from.</summary>
    public McpServer Server { get; }

    /// <summary>Tool names or globs to include; null is all.</summary>
    public IReadOnlyList<string>? Tools { get; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default) => FilterAsync(Server.ToolsAsync(cancellationToken));

    private async Task<IReadOnlyList<ToolDef>> FilterAsync(Task<IReadOnlyList<ToolDef>> pending)
    {
        var tools = await pending.ConfigureAwait(false);
        if (Tools is null)
        {
            return tools;
        }

        return tools.Where(tool => Tools.Any(pattern => FnMatch.Match(tool.Name, pattern))).ToArray();
    }
}

/// <summary>Port of Python's <c>fnmatch.fnmatch</c> as used on POSIX (case-sensitive, no path semantics).</summary>
internal static partial class FnMatch
{
    /// <summary>Whether <paramref name="name"/> matches the shell-style <paramref name="pattern"/> (<c>*</c>, <c>?</c>, <c>[seq]</c>, <c>[!seq]</c>).</summary>
    public static bool Match(string name, string pattern) => Regex.IsMatch(name, Translate(pattern), RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Port of <c>fnmatch.translate</c> (anchored with <c>^</c> and <c>\z</c>).</summary>
    public static string Translate(string pattern)
    {
        var result = new StringBuilder("^");
        var i = 0;
        var n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i++];
            switch (c)
            {
                case '*':
                    result.Append(".*");
                    break;
                case '?':
                    result.Append('.');
                    break;
                case '[':
                    var j = i;
                    if (j < n && pattern[j] == '!')
                    {
                        j++;
                    }

                    if (j < n && pattern[j] == ']')
                    {
                        j++;
                    }

                    while (j < n && pattern[j] != ']')
                    {
                        j++;
                    }

                    if (j >= n)
                    {
                        result.Append("\\[");
                    }
                    else
                    {
                        var stuff = pattern[i..j].Replace("\\", "\\\\", StringComparison.Ordinal);
                        i = j + 1;
                        if (stuff.StartsWith('!'))
                        {
                            stuff = "^" + stuff[1..];
                        }
                        else if (stuff.StartsWith('^'))
                        {
                            stuff = "\\" + stuff;
                        }

                        result.Append('[').Append(stuff).Append(']');
                    }

                    break;
                default:
                    result.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return result.Append("\\z").ToString();
    }
}
