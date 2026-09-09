using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.EvalSet;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using EvalModel = InspectAzureAI.Eval.Model.Model;
using EvalRunner = InspectAzureAI.Eval.Runner.Eval;

namespace InspectAzureAI.Eval.Tests;

public class ProviderMigrationTests
{
    private static EvalTask TaskSpec() => new() { Name = "migration", Dataset = new MemoryDataset([new Sample("hi")]) };
    [Fact]
    public async Task legacy_matching_is_foundry_only_and_preserves_roles_and_other_identity_fields()
    {
        var task = TaskSpec();
        var model = new EvalModel(new IdentityApi("gpt-5.6-sol", "openai/azure/gpt-5.6-sol", true));
        var role = new EvalModel(new IdentityApi("judge", "anthropic/azure/judge", true));
        var roles = ModelRoles.Resolve(new Dictionary<string, object> { ["grader"] = role });
        var log = await EvalRunner.RunAsync(task, new() { Model = model, ModelRoles = new Dictionary<string, object> { ["grader"] = role } });
        var qualified = log with { Eval = log.Eval with { Model = model.Api.QualifiedModelName, ModelRoles = new Dictionary<string, IReadOnlyList<ModelConfig>> { ["grader"] = [new("anthropic/azure/judge") { Config = role.Config }] } } };
        var legacy = log with { Eval = log.Eval with { Model = model.Name, ModelRoles = new Dictionary<string, IReadOnlyList<ModelConfig>> { ["grader"] = [new("judge") { Config = role.Config }] } } };
        var candidate = new ResolvedTask(task, model, roles, 0, "new-id", TaskIdentifier.Compute(qualified));
        var historical = new EvalSetLog("old.eval", DateTimeOffset.UtcNow, legacy, TaskIdentifier.Compute(legacy));
        Assert.Same(candidate, TaskIdentityMatcher.Match(historical, [candidate]));
        Assert.Equal(candidate.Identifier, EvalSetLogs.ValidateEvalSetPrerequisites([candidate], [historical], false)[0].TaskIdentifier);
        Assert.Equal(log.Eval.TaskId, EvalSetInfo.Build("set", [candidate], [historical]).Tasks[0].TaskId);
        Assert.Equal(model.Name, historical.Header.Eval.Model);
        var direct = new EvalModel(new IdentityApi(model.Name, "openai/gpt-5.6-sol", false));
        Assert.Null(TaskIdentityMatcher.Match(historical, [candidate with { Model = direct }]));
        var different = historical with { Header = legacy with { Eval = legacy.Eval with { TaskVersion = "different" } } };
        Assert.Null(TaskIdentityMatcher.Match(different, [candidate]));
        Assert.Throws<PrerequisiteError>(() => TaskIdentityMatcher.Match(historical, [candidate, candidate with { Id = "another" }]));
        Assert.Equal(3, TaskIdentifier.Version);
    }
    [Fact]
    public async Task direct_args_are_redacted_and_live_identity_matches_persisted_log()
    {
        var handler = new ReplyHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"gpt-5.6-sol\",\"status\":\"completed\",\"output\":[]}", Encoding.UTF8, "application/json") });
        using var api = new OpenAIModelApi("gpt-5.6-sol", "https://one.invalid/v1", modelArgs: new Dictionary<string, object?> { ["api_key"] = "DO_NOT_PERSIST", ["responses_api"] = true, ["extra_body"] = new JsonObject { ["custom"] = "value" } }, settings: new() { Handler = handler });
        var model = new EvalModel(api);
        var task = TaskSpec();
        var log = await EvalRunner.RunAsync(task, new() { Model = model, ModelRoles = new Dictionary<string, object> { ["judge"] = model } });
        var json = JsonSerializer.Serialize(log, EvalLogWriter.Options);
        Assert.DoesNotContain("DO_NOT_PERSIST", json);
        Assert.Equal("openai/gpt-5.6-sol", log.Eval.Model);
        var roundTrip = JsonSerializer.Deserialize<EvalLog>(json, EvalLogWriter.Options)!;
        Assert.Equal(TaskIdentifier.Compute(task, model, ModelRoles.Resolve(new Dictionary<string, object> { ["judge"] = model }), new()), TaskIdentifier.Compute(roundTrip));
        Assert.DoesNotContain("api_key", handler.Body!);
        Assert.DoesNotContain("responses_api", handler.Body!);
    }
    [Fact]
    public async Task auth_retry_reloads_typed_credentials_and_endpoints_partition_cache_answers()
    {
        var requests = 0;
        var key = "old";
        var handler = new ReplyHandler(request =>
        {
            requests++;
            if (request.Headers.Authorization!.Parameter == "old") { key = "new"; return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"message\":\"expired\"}}") }; }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"gpt-5.6-sol\",\"status\":\"completed\",\"output\":[]}", Encoding.UTF8, "application/json") };
        });
        using var api = new OpenAIModelApi("gpt-5.6-sol", "https://one.invalid/v1", "test", settings: new() { Handler = handler, ApiKeyOverride = (_, _) => key });
        var model = new EvalModel(api, retry: new() { Delay = (_, _) => Task.CompletedTask });
        await model.GenerateAsync("hi");
        Assert.Equal(2, requests);
        using var other = new OpenAIModelApi(api.ModelName, "https://two.invalid/v1", "test");
        CacheEntry Entry(IModelApi provider) => new(provider.BaseUrl, new(), [new ChatMessageUser("hi")], ModelIdentity.ForCache(provider), CachePolicy.Default, ToolChoice.Auto, []);
        Assert.NotEqual(Entry(api).Key, Entry(other).Key);
        Assert.NotEqual(api.ConnectionKey(), other.ConnectionKey());
        var foundry = new IdentityApi(api.ModelName, "openai/azure/" + api.ModelName, true);
        Assert.Equal(api.ModelName, Entry(foundry).Model);
        Assert.NotEqual(Entry(api).Model, Entry(foundry).Model);
    }
    [Fact]
    public async Task old_foundry_log_resumes_partial_samples_then_reuses_the_completed_task()
    {
        var directory = Path.Combine(Path.GetTempPath(), "provider-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var task = TaskSpec() with { Dataset = new MemoryDataset([new Sample("first"), new Sample("second")]) };
            var first = new ScriptedModelApi(ScriptedTurn.Text("first answer"), ScriptedTurn.Throw(new InvalidOperationException("failed sample")));
            EvalSetOptions Options(ScriptedModelApi script) => new() { Eval = new() { Model = new EvalModel(new FoundryScript(script)), LogDir = directory, MaxSamples = 1 }, RetryAttempts = 0, RetryCleanup = false };
            var run = await InspectAzureAI.Eval.Runner.EvalSet.EvalSet.RunAsync([task], Options(first));
            var old = Assert.Single(run.Logs);
            Assert.Equal(EvalStatus.Error, old.Status);
            var path = Path.Combine(directory, "2020-01-01T00-00-00_migration_old.eval");
            File.Delete(old.Location!);
            EvalLogWriter.Write(old with { Eval = old.Eval with { Model = "deployment" } }, path);
            var bytes = File.ReadAllBytes(path);
            var second = new ScriptedModelApi(ScriptedTurn.Text("second answer"));
            var resumed = await InspectAzureAI.Eval.Runner.EvalSet.EvalSet.RunAsync([task], Options(second));
            Assert.True(resumed.Success);
            Assert.Single(second.Requests);
            Assert.Equal(old.Eval.TaskId, resumed.Logs[0].Eval.TaskId);
            Assert.Equal(old.Samples![0].Uuid, resumed.Logs[0].Samples![0].Uuid);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            var idle = new ScriptedModelApi { ThrowWhenExhausted = true };
            var done = await InspectAzureAI.Eval.Runner.EvalSet.EvalSet.RunAsync([task], Options(idle));
            Assert.True(done.Success);
            Assert.Empty(idle.Requests);
            Assert.Equal(old.Eval.TaskId, EvalSetInfo.Read(directory)!.Tasks[0].TaskId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class FoundryScript(ScriptedModelApi script) : IModelApi
    {
        public string ModelName => "deployment";
        public string QualifiedModelName => "openai/azure/deployment";
        public bool IsFoundry => true;
        public string ProviderName => "openai";
        public int? MaxTokens() => script.MaxTokens();
        public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice choice, GenerateConfig config, CancellationToken ct = default) => script.GenerateAsync(input, tools, choice, config, ct);
        public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice choice, GenerateConfig config, StreamHandler? onStream, CancellationToken ct = default) => script.GenerateAsync(input, tools, choice, config, onStream, ct);
    }
    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return reply(request);
        }
    }
    private sealed class IdentityApi(string name, string qualified, bool foundry) : IModelApi
    {
        public string ModelName => name;
        public string QualifiedModelName => qualified;
        public bool IsFoundry => foundry;
        public int? MaxTokens() => null;
        public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice choice, GenerateConfig config, CancellationToken ct = default) => GenerateAsync(input, tools, choice, config, null, ct);
        public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice choice, GenerateConfig config, StreamHandler? onStream, CancellationToken ct = default) => Task.FromResult(new GenerateResult(ModelOutput.FromContent(name, "ok"), null, ModelCall.Create(new JsonObject())));
    }
}
