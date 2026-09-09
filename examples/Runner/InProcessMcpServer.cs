using System.IO.Pipelines;
using System.Threading.Channels;
using InspectAzureAI.Eval.Tools.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkMcpServer = ModelContextProtocol.Server.McpServer;
using SdkMcpServerOptions = ModelContextProtocol.Server.McpServerOptions;
using SdkMcpServerTool = ModelContextProtocol.Server.McpServerTool;
using SdkPrimitiveCollection = ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>;
using SdkStreamServerTransport = ModelContextProtocol.Server.StreamServerTransport;

namespace InspectAzureAI.Examples.Runner;

/// <summary>
/// An MCP server hosted inside this process for the offline runs: every connection of the client transport starts
/// a fresh SDK server (<see cref="SdkMcpServer"/>) joined to the client by two pipes, so the engine's
/// <see cref="McpServerLocal"/> talks MCP end to end without a child process or the network. Stands in for the
/// stdio and HTTP servers the Python examples reach (<c>mcp_server_git</c>, DeepWiki), which the engine otherwise
/// spawns or connects to through the same <see cref="McpServerLocal"/>.
/// </summary>
public sealed class InProcessMcpServer : IClientTransport
{
    private readonly string _name;
    private readonly Func<IEnumerable<SdkMcpServerTool>> _tools;
    private int _connects;

    /// <summary>A server named <paramref name="name"/> serving <paramref name="tools"/> (created afresh per connection).</summary>
    public InProcessMcpServer(string name, Func<IEnumerable<SdkMcpServerTool>> tools)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tools);
        _name = name;
        _tools = tools;
    }

    /// <inheritdoc />
    public string Name => _name;

    /// <summary>How many sessions have been opened (one per sample under <c>react</c>).</summary>
    public int Connects => Volatile.Read(ref _connects);

    /// <summary>The engine-side server over this transport: what <c>mcp_server_stdio</c> / <c>mcp_server_http</c> return, minus the process or socket.</summary>
    public McpServerLocal AsMcpServer(bool events = true) => new(() => this, _name, events);

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connects);
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = new SdkMcpServerOptions
        {
            ServerInfo = new Implementation { Name = _name, Version = "1" },
            ToolCollection = new SdkPrimitiveCollection(StringComparer.Ordinal),
        };
        foreach (var tool in _tools())
        {
            options.ToolCollection.Add(tool);
        }

        var server = SdkMcpServer.Create(new SdkStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), _name, null), options, null, null);
        var run = server.RunAsync(CancellationToken.None);
        var client = await new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), null).ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new Session(client, server, run, toServer, toClient);
    }

    private sealed class Session(ITransport client, SdkMcpServer server, Task run, Pipe toServer, Pipe toClient) : ITransport
    {
        public string? SessionId => client.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => client.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => client.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync().ConfigureAwait(false);
            toServer.Writer.Complete();
            toClient.Writer.Complete();
            await server.DisposeAsync().ConfigureAwait(false);
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the pipes closed under the server
            }
        }
    }
}
