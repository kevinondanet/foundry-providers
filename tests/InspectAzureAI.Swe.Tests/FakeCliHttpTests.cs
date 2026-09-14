using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.Swe.Tests;

/// <summary>The shared fake-CLI helpers: SSE frames, the strict HTTP handler, the TOML reader, the request log and env helpers.</summary>
public class FakeCliHttpTests
{
    private static async Task<List<(string? Event, string Data)>> FramesAsync(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var frames = new List<(string? Event, string Data)>();
        await foreach (var frame in SseFrames.ReadAsync(stream))
        {
            frames.Add(frame);
        }

        return frames;
    }

    [Fact]
    public async Task sse_frames_join_multi_line_data_and_stop_at_done()
    {
        const string text = """
            : keepalive
            event: response.created
            data: {"a":1}

            data: line one
            data: line two
            id: 7

            event: only-an-event

            data: [DONE]

            data: never read

            """;

        var frames = await FramesAsync(text);

        Assert.Equal(
            new (string? Event, string Data)[] { ("response.created", "{\"a\":1}"), (null, "line one\nline two"), (null, SseFrames.Done) },
            frames);
    }

    [Fact]
    public async Task sse_frames_accept_crlf_missing_spaces_and_a_trailing_frame()
    {
        var frames = await FramesAsync("event:message_start\r\ndata:{\"type\":\"message_start\"}\r\n\r\ndata:  two spaces\r\ndata:");

        Assert.Equal(
            new (string? Event, string Data)[] { ("message_start", "{\"type\":\"message_start\"}"), (null, " two spaces\n") },
            frames);
    }

    [Fact]
    public async Task strict_handler_serves_registered_uris_and_throws_on_and_records_the_rest()
    {
        var handler = new StrictHttpHandler().Register("https://api.test/ok", _ => StrictHttpHandler.Json("""{"ok":true}"""));
        using var client = new HttpClient(handler);

        var body = await client.GetStringAsync("https://api.test/ok");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://api.test/missing?x=1"));

        Assert.Equal("""{"ok":true}""", body);
        Assert.Equal("unexpected request GET https://api.test/missing?x=1", ex.Message);
        Assert.Equal(["https://api.test/ok", "https://api.test/missing?x=1"], handler.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        Assert.Equal("https://api.test/missing?x=1", Assert.Single(handler.Unexpected).RequestUri!.AbsoluteUri);
        Assert.Equal(1, handler.CountOf("https://api.test/ok"));
    }

    [Fact]
    public void mini_toml_reads_quoted_segments_inline_tables_and_scalars()
    {
        const string text = """
            # written by Toml.Write
            web_search = "live" # trailing comment
            features.goals = true
            count = 3_600
            ratio = 1.5
            list = ["a", 'b', 2, [true]]

            [mcp_servers."my server"]
            url = "http://127.0.0.1:1/mcp/my%20server"
            http_headers = { Authorization = "Bearer t", "X Trace" = "a\"bé" }
            empty = {}

            [a.'lit'.b-c]
            x = false
            """;

        var tables = MiniToml.Parse(text);

        Assert.Equal(["", "mcp_servers.my server", "a.lit.b-c"], tables.Keys);
        Assert.Equal("live", tables[""]["web_search"]!.GetValue<string>());
        Assert.True(tables[""]["features.goals"]!.GetValue<bool>());
        Assert.Equal(3600L, tables[""]["count"]!.GetValue<long>());
        Assert.Equal(1.5, tables[""]["ratio"]!.GetValue<double>());
        Assert.Equal("""["a","b",2,[true]]""", tables[""]["list"]!.ToJsonString());
        var server = tables["mcp_servers.my server"];
        Assert.Equal("http://127.0.0.1:1/mcp/my%20server", server["url"]!.GetValue<string>());
        var headers = Assert.IsType<JsonObject>(server["http_headers"]);
        Assert.Equal("Bearer t", headers["Authorization"]!.GetValue<string>());
        Assert.Equal("a\"bé", headers["X Trace"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonObject>(server["empty"]));
        Assert.False(tables["a.lit.b-c"]["x"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("a = 1\na = 2")]
    [InlineData("[t]\nx = 1\n[t]")]
    [InlineData("a = \"unterminated")]
    [InlineData("a = what")]
    [InlineData("= 1")]
    [InlineData("a = 1 trailing")]
    public void mini_toml_rejects_what_the_writer_never_emits(string text)
    {
        Assert.Throws<FormatException>(() => MiniToml.Parse(text));
    }

    [Fact]
    public void request_log_is_thread_safe_and_snapshots_copy()
    {
        var log = new FakeCliRequestLog();

        Parallel.For(0, 200, i => log.Add(new FakeCliRequest("POST", "/v1/responses", "gpt-5.4", 200, i, "Bearer t")));
        var snapshot = log.Snapshot();
        log.Add(new FakeCliRequest("POST", "/mcp/secrets", null, 401, 200, null));

        Assert.Equal(200, snapshot.Count);
        Assert.Equal(Enumerable.Range(0, 200), snapshot.Select(r => r.Turn).Order());
        Assert.Equal(201, log.Count);
        Assert.Equal(new FakeCliRequest("POST", "/mcp/secrets", null, 401, 200, null), log.Snapshot()[^1]);
    }

    [Fact]
    public void env_helpers_read_the_launch_env_and_build_authenticated_clients()
    {
        var call = new FakeExecCall(["codex", "exec"], null, "/workspace", new Dictionary<string, string> { ["OPENAI_BASE_URL"] = "http://127.0.0.1:5555/v1" }, "agent", null);

        Assert.Equal("http://127.0.0.1:5555/v1", FakeCliEnv.Require(call, "OPENAI_BASE_URL"));
        Assert.Contains("OPENAI_API_KEY", Assert.Throws<InvalidOperationException>(() => FakeCliEnv.Require(call, "OPENAI_API_KEY")).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => FakeCliEnv.Require(call with { Env = null }, "OPENAI_BASE_URL"));

        using var bearer = FakeCliEnv.BearerClient("http://127.0.0.1:5555/v1", "tok");
        using var apiKey = FakeCliEnv.ApiKeyClient("http://127.0.0.1:5555/", "key");

        Assert.Equal("Bearer tok", bearer.DefaultRequestHeaders.Authorization!.ToString());
        Assert.Equal(new Uri("http://127.0.0.1:5555/v1/responses"), new Uri(bearer.BaseAddress!, "responses"));
        Assert.Equal(["key"], apiKey.DefaultRequestHeaders.GetValues("x-api-key"));
        Assert.Equal(new Uri("http://127.0.0.1:5555/v1/messages"), new Uri(apiKey.BaseAddress!, "v1/messages"));
    }
}
