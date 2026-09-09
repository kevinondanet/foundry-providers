using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// The sandbox service protocol (port of <c>util/_sandbox/service.py</c>) against a <see cref="ContainerSimulator"/>:
/// directory setup, the generated client, request validation and error responses, retries of partial writes,
/// the polling loop and its prerequisites.
/// </summary>
public class SandboxServiceTests
{
    private const string Root = SandboxService.ServicesDir + "/calc";

    private const string Requests = Root + "/requests";

    private const string Responses = Root + "/responses";

    private static Dictionary<string, SandboxServiceMethod> Methods() => new(StringComparer.Ordinal)
    {
        ["add"] = (parameters, _) => Task.FromResult<JsonNode?>(parameters["a"]!.GetValue<int>() + parameters["b"]!.GetValue<int>()),
        ["boom"] = (_, _) => throw new InvalidOperationException("boom"),
        ["echo"] = (parameters, _) => Task.FromResult<JsonNode?>(parameters["text"]?.DeepClone()),
    };

    private static async Task<SandboxService> StartedAsync(ContainerSimulator sim, string? user = null, string? instance = null)
    {
        var service = new SandboxService("calc", sim, user, instance);
        foreach (var (name, method) in Methods())
        {
            service.AddMethod(name, method);
        }

        await service.StartAsync();
        return service;
    }

    private static string Request(string id, string method, JsonObject parameters) =>
        new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString();

    private static JsonObject Response(ContainerSimulator sim, string id) =>
        JsonNode.Parse(sim.Text($"{Responses}/{id}.json") ?? throw new InvalidOperationException($"no response for {id}")) as JsonObject
        ?? throw new InvalidOperationException("response is not an object");

    [Fact]
    public void the_client_script_defines_the_call_functions_over_the_service_directory()
    {
        var script = SandboxService.ClientScript("foo");

        Assert.StartsWith("\nfrom typing import Any\n\ndef call_foo(method: str, **params: Any) -> Any:", script, StringComparison.Ordinal);
        Assert.Contains("async def call_foo_async(method: str, **params: Any) -> Any:", script, StringComparison.Ordinal);
        Assert.Contains("def _write_foo_request(method: str, **params: Any) -> str:", script, StringComparison.Ordinal);
        Assert.Contains("request_data = dict(id=request_id, method=method, params=params)", script, StringComparison.Ordinal);
        Assert.Contains("sleep(0.1)", script, StringComparison.Ordinal);
        Assert.Contains("service_dir = Path(\"/var/tmp/sandbox-services/foo\")", script, StringComparison.Ordinal);
        Assert.Contains("os.environ.get(\"FOO_INSTANCE\", None)", script, StringComparison.Ordinal);
        Assert.Contains("if response.get(\"error\", None) is not None:", script, StringComparison.Ordinal);
        Assert.EndsWith("return service_dir / subdir\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void service_names_and_instances_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new SandboxService("not valid", null));
        Assert.Throws<ArgumentException>(() => new SandboxService("1abc", null));
        Assert.Throws<ArgumentException>(() => new SandboxService("ok", null, instance: "../x"));
        Assert.Throws<ArgumentException>(() => new SandboxService("ok", null, instance: ""));

        Assert.Equal(SandboxService.ServicesDir + "/ok", new SandboxService("ok", null).ServiceDir);
        Assert.Equal(SandboxService.ServicesDir + "/ok/inst-1", new SandboxService("ok", null, instance: "inst-1").ServiceDir);
    }

    [Fact]
    public async Task start_prepares_the_directories_as_the_user_and_writes_the_client_module()
    {
        var sim = new ContainerSimulator();

        var service = await StartedAsync(sim, user: "agent");

        Assert.Equal(Requests, service.RequestsPath);
        Assert.Equal(Responses, service.ResponsesPath);
        Assert.Equal(Root + "/calc.py", service.ClientScriptPath);
        Assert.Equal(SandboxService.ClientScript("calc"), sim.Text(Root + "/calc.py"));

        var argv = sim.Calls.Select(call => (string.Join(" ", call.Cmd), call.User)).ToList();
        Assert.Equal(
            [
                ("sh -c mkdir -p /var/tmp/sandbox-services && chmod 1777 /var/tmp/sandbox-services 2>/dev/null; true", null),
                ("mkdir -p " + Root, "agent"),
                ("test -O " + Root, "agent"),
                ("rm -rf " + Requests, "agent"),
                ("mkdir -p " + Requests, "agent"),
                ("rm -rf " + Responses, "agent"),
                ("mkdir -p " + Responses, "agent"),
                ("tee -- " + Root + "/calc.py", "agent"),
            ],
            argv);
        Assert.All(sim.Calls.Skip(1), call => Assert.Equal(TimeSpan.FromSeconds(600), call.Timeout));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
    }

    [Fact]
    public async Task an_unwritable_parent_or_a_squatted_directory_is_a_prerequisite_error()
    {
        var squatted = new ContainerSimulator { Override = call => call.Cmd[0] == "test" && call.Cmd[1] == "-O" ? FakeSandboxEnvironment.Fail(1) : null };
        var squat = await Assert.ThrowsAsync<PrerequisiteError>(() => new SandboxService("calc", squatted, "agent").StartAsync());
        Assert.Contains("'/var/tmp/sandbox-services/calc' exists but is not owned by user 'agent'", squat.Message, StringComparison.Ordinal);

        var unwritable = new ContainerSimulator
        {
            Override = call => call.Cmd[0] switch
            {
                "mkdir" when call.Cmd[^1] == Root => FakeSandboxEnvironment.Fail(1, "Permission denied"),
                "test" when call.Cmd[1] == "-w" => FakeSandboxEnvironment.Fail(1),
                _ => null,
            },
        };
        var parent = await Assert.ThrowsAsync<PrerequisiteError>(() => new SandboxService("calc", unwritable).StartAsync());
        Assert.Contains("not writable by user 'the sandbox default user'", parent.Message, StringComparison.Ordinal);

        var noPython = new ContainerSimulator { PythonInstalled = false };
        var python = await Assert.ThrowsAsync<PrerequisiteError>(() => SandboxService.RunAsync("calc", Methods(), () => true, noPython));
        Assert.Equal("The calc requires that Python be installed in the sandbox.", python.Message);
    }

    [Fact]
    public async Task requests_are_answered_and_removed_with_a_result_or_an_error()
    {
        var sim = new ContainerSimulator();
        var service = await StartedAsync(sim);
        sim.PutFile($"{Requests}/r1.json", Request("r1", "add", new JsonObject { ["a"] = 1, ["b"] = 2 }));
        sim.PutFile($"{Requests}/r2.json", Request("r2", "nope", new JsonObject()));
        sim.PutFile($"{Requests}/r3.json", Request("r3", "boom", new JsonObject()));
        sim.PutFile($"{Requests}/r4.json", Request("r4", "add", new JsonObject()).Replace("\"params\":{}", "\"params\":[]", StringComparison.Ordinal));
        sim.PutFile($"{Requests}/r5.json", Request("other", "add", new JsonObject()));
        sim.PutFile($"{Requests}/r6.json", "[1, 2]");
        sim.PutFile($"{Requests}/r7.json", "{\"id\": \"r7\", \"params\": {}}");
        sim.PutFile($"{Requests}/r8.json", Request("r8", "echo", new JsonObject { ["text"] = "héllo" }));

        await service.HandleRequestsAsync();

        Assert.Empty(sim.FilesUnder(Requests));
        Assert.Equal(0, service.InFlightCount);

        var r1 = Response(sim, "r1");
        Assert.Equal("r1", r1["id"]!.GetValue<string>());
        Assert.Equal(3, r1["result"]!.GetValue<int>());
        Assert.Null(r1["error"]);
        Assert.True(r1.ContainsKey("error"));
        Assert.Equal("Unknown method 'nope'", Response(sim, "r2")["error"]!.GetValue<string>());
        Assert.Equal("Error calling method boom: boom", Response(sim, "r3")["error"]!.GetValue<string>());
        Assert.StartsWith("params not passed or not a dict", Response(sim, "r4")["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Service request id does not match request filename", Response(sim, "r5")["error"]!.GetValue<string>());
        Assert.Equal("Service request is not a dict (type=<class 'list'>)", Response(sim, "r6")["error"]!.GetValue<string>());
        Assert.Equal("Service method not passed or not a string (type=<class 'NoneType'>)", Response(sim, "r7")["error"]!.GetValue<string>());
        Assert.Equal("héllo", Response(sim, "r8")["result"]!.GetValue<string>());
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "find" && call.Cmd[1] == Requests && call.Cmd[^1] == "-print0");
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "cat" && call.Cmd[^1] == $"{Requests}/r1.json");
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "rm" && call.Cmd[1] == "-f" && call.Cmd[^1] == $"{Requests}/r1.json");
    }

    [Fact]
    public async Task an_invalid_filename_is_discarded_and_a_partial_write_is_retried()
    {
        var sim = new ContainerSimulator();
        var service = await StartedAsync(sim);
        sim.PutFile($"{Requests}/bad name.json", Request("bad name", "add", new JsonObject()));
        sim.PutFile($"{Requests}/partial.json", "{\"id\": \"partial\", \"method\": \"add\", \"par");

        await service.HandleRequestsAsync();

        Assert.Equal([$"{Requests}/partial.json"], sim.FilesUnder(Requests));
        Assert.Empty(sim.FilesUnder(Responses));
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "wc" && call.Cmd[^1] == $"{Requests}/partial.json");

        // completing the write lets the next poll serve it
        sim.PutFile($"{Requests}/partial.json", Request("partial", "add", new JsonObject { ["a"] = 2, ["b"] = 2 }));
        await service.HandleRequestsAsync();
        Assert.Equal(4, Response(sim, "partial")["result"]!.GetValue<int>());
    }

    [Fact]
    public async Task run_polls_until_the_predicate_holds_and_drains_in_flight_handlers()
    {
        var sim = new ContainerSimulator();
        var served = 0;
        var methods = new Dictionary<string, SandboxServiceMethod>(StringComparer.Ordinal)
        {
            ["ping"] = async (_, cancellationToken) =>
            {
                await Task.Delay(30, cancellationToken);
                Interlocked.Increment(ref served);
                return "pong";
            },
        };

        var run = SandboxService.RunAsync("calc", methods, () => Volatile.Read(ref served) >= 2, sim, user: "agent", pollingInterval: TimeSpan.FromMilliseconds(10));
        var first = sim.CallAsync("ping", service: "calc");
        var second = sim.CallAsync("ping", service: "calc");

        Assert.Equal("pong", (await first)!.GetValue<string>());
        Assert.Equal("pong", (await second)!.GetValue<string>());
        await run;
        Assert.Equal(2, served);
        Assert.All(sim.Calls.Where(call => call.Cmd[0] is "find" or "cat" or "tee" or "rm"), call => Assert.Equal("agent", call.User));
    }

    [Fact]
    public async Task run_is_cancelled_with_its_token()
    {
        var sim = new ContainerSimulator();
        using var cancel = new CancellationTokenSource();

        var run = SandboxService.RunAsync("calc", Methods(), () => false, sim, pollingInterval: TimeSpan.FromMilliseconds(10), cancellationToken: cancel.Token);
        await Task.Delay(50);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task an_instance_nests_the_service_under_its_own_directory()
    {
        var sim = new ContainerSimulator();

        var service = await StartedAsync(sim, instance: "sample-7");

        Assert.Equal(Root + "/sample-7/requests", service.RequestsPath);
        Assert.True(sim.HasFile(Root + "/sample-7/calc.py"));
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "test" && call.Cmd[^1] == Root);
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "test" && call.Cmd[^1] == Root + "/sample-7");
    }
}
