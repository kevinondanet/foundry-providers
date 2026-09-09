using System.Reflection;
using System.Text.Json.Nodes;
using Azure.Core;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Images;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;

namespace InspectAzureAI.Examples.Tests.Images;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/images/images.py</c> (<see cref="ImagesExample"/>): the dataset's image parts
/// resolve to the PNGs beside <c>images.jsonl</c>, the task's shape, the scripted model, the eval run end to end
/// without a network (through <c>Eval.RunAsync</c> and the examples runner), and the Azure route inlining the PNGs
/// as data URIs over a canned transport.
/// </summary>
public sealed class ImagesTests : IDisposable
{
    private const string BallonsQuestion = "How many ballons are in this picture?";

    private const string BikeQuestion = "What is this a picture of?";

    /// <summary>The example's data files, linked next to the test assembly by the test project.</summary>
    private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "images");

    private static readonly string DatasetPath = Path.Combine(DataDirectory, "images.jsonl");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "images-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // the dataset and the task (port of @task def images)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_dataset_resolves_the_image_parts_to_the_pngs_beside_it()
    {
        Assert.True(File.Exists(DatasetPath), $"images.jsonl was not linked into the test output: {DatasetPath}");

        var dataset = Datasets.Json(DatasetPath);

        Assert.Equal("images", dataset.Name);
        Assert.Equal(2, dataset.Count);
        AssertImageSample(dataset[0], BallonsQuestion, "ballons.png", new Target("3"));
        AssertImageSample(dataset[1], BikeQuestion, "bike.png", new Target(["bike", "bicycle"]));
    }

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_task()
    {
        var task = ImagesExample.Build(DatasetPath);

        Assert.Equal("images", task.Name);
        Assert.Equal("images", task.Dataset.Name);
        Assert.Equal(2, task.Dataset.Count);
        Assert.Equal("match", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
        // the task's copy of the dataset carries the PNGs inlined, as Python's pre-run media step leaves them
        foreach (var (sample, file) in new[] { (task.Dataset[0], "ballons.png"), (task.Dataset[1], "bike.png") })
        {
            var user = Assert.IsType<ChatMessageUser>(Assert.Single(sample.Input.Messages!));
            var image = Assert.IsType<ContentImage>(user.ContentList[1]);
            Assert.StartsWith("data:image/png;base64,", image.Image, StringComparison.Ordinal);
            Assert.Equal(File.ReadAllBytes(Path.Combine(DataDirectory, file)), Convert.FromBase64String(image.Image["data:image/png;base64,".Length..]));
        }
        Assert.StartsWith("\nFor the following exercise, it is important that you answer with only a single word or numeric value in brackets.", ImagesExample.SystemMessage, StringComparison.Ordinal);
        Assert.EndsWith("just a single value in brackets.\n", ImagesExample.SystemMessage, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ImagesExample.Build(""));
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_and_finds_the_dataset_beside_the_assembly()
    {
        var method = typeof(ImagesExample).GetMethod(nameof(ImagesExample.Images));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("images", method.GetCustomAttribute<TaskAttribute>()!.Name);
        Assert.Equal(DatasetPath, ImagesExample.DefaultDatasetPath());
        Assert.Equal(2, ImagesExample.Images().Dataset.Count);
    }

    [Fact]
    public void the_example_is_registered_without_a_sandbox_and_needs_a_vision_model()
    {
        var example = Assert.IsType<ImagesExample>(ExampleRegistry.Default.Get("images"));

        Assert.Equal("images", Assert.Single(example.Tasks).Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Contains("vision", example.Defaults.ModelHint);
        Assert.NotEmpty(example.Deviations);
        var ctx = new ExampleContext(DataDirectory, null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);
        Assert.Null(example.FakeSandbox(ctx));
        Assert.Equal(FakeImagesModel.ModelName, example.CreateFakeModel(ctx).Name);
        Assert.Equal(2, example.Tasks[0].Build(ctx).Dataset.Count);
    }

    [Fact]
    public void materialising_inlines_local_image_files_and_leaves_everything_else_alone()
    {
        var png = Path.Combine(DataDirectory, "bike.png");

        Assert.StartsWith("data:image/png;base64,", SampleImages.Materialize(png), StringComparison.Ordinal);
        Assert.Equal("data:image/png;base64,AAAA", SampleImages.Materialize("data:image/png;base64,AAAA"));
        Assert.Equal("https://example.com/a.png", SampleImages.Materialize("https://example.com/a.png"));
        Assert.Equal(Path.Combine(DataDirectory, "missing.png"), SampleImages.Materialize(Path.Combine(DataDirectory, "missing.png")));
        Assert.Equal("", SampleImages.Materialize(""));
        Assert.Equal("image/jpeg", SampleImages.MimeType("photo.JPG"));
        Assert.Equal("image/png", SampleImages.MimeType("unknown.bin"));

        var text = new Sample("plain text") { Target = "x" };
        Assert.Same(text, SampleImages.Materialize(text));
        var sample = new Sample(new ChatMessage[] { new ChatMessageUser(new Content[] { new ContentText("q"), new ContentImage(png) }), new ChatMessageAssistant("a") });
        var materialized = SampleImages.Materialize(sample);
        var user = Assert.IsType<ChatMessageUser>(materialized.Input.Messages![0]);
        Assert.Equal("q", Assert.IsType<ContentText>(user.ContentList[0]).Text);
        Assert.StartsWith("data:image/png;base64,", Assert.IsType<ContentImage>(user.ContentList[1]).Image, StringComparison.Ordinal);
        Assert.Equal("a", materialized.Input.Messages[1].Text);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_scripted_model_answers_by_image_file_then_by_question()
    {
        Assert.Equal("[3]", FakeImagesModel.Answer(new ChatMessageUser(new Content[] { new ContentText(BallonsQuestion), new ContentImage("/anywhere/ballons.png") })));
        Assert.Equal("[bike]", FakeImagesModel.Answer(new ChatMessageUser(new Content[] { new ContentText(BikeQuestion), new ContentImage("C:\\pictures\\bike.png") })));
        // an inlined image says nothing about the file, so the question decides
        Assert.Equal("[3]", FakeImagesModel.Answer(new ChatMessageUser(new Content[] { new ContentText(BallonsQuestion), new ContentImage("data:image/png;base64,AAAA") })));
        Assert.Equal("[bike]", FakeImagesModel.Answer(new ChatMessageUser(new Content[] { new ContentText(BikeQuestion), new ContentImage("data:image/png;base64,AAAA") })));
        Assert.Equal("[unknown]", FakeImagesModel.Answer(new ChatMessageUser("Describe the weather.")));
        Assert.Equal("[unknown]", FakeImagesModel.Respond([new ChatMessageSystem("system only")], []).Completion);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_task_runs_offline_and_matches_both_answers()
    {
        var log = await Run(FakeImagesModel.Create());

        var samples = log.Samples!;
        Assert.Equal(2, samples.Count);
        var ballons = Assert.Single(samples, sample => sample.Input.ToString() == BallonsQuestion);
        var bike = Assert.Single(samples, sample => sample.Input.ToString() == BikeQuestion);
        Assert.Equal("[3]", ballons.Output.Completion);
        Assert.Equal("[bike]", bike.Output.Completion);
        foreach (var sample in samples)
        {
            Assert.Null(sample.Error);
            var score = sample.Scores!["match"];
            Assert.Equal("C", score.Text);
            Assert.Equal(sample.Output.Completion, score.Answer);
            // system_message put the prompt first; the user message kept its text and image parts
            Assert.Equal(["system", "user", "assistant"], sample.Messages.Select(message => message.Role));
            Assert.Equal(ImagesExample.SystemMessage, sample.Messages[0].Text);
            var user = Assert.IsType<ChatMessageUser>(sample.Messages[1]);
            Assert.Equal(2, user.ContentList.Count);
            Assert.StartsWith("data:image/png;base64,", Assert.IsType<ContentImage>(user.ContentList[1]).Image, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task the_azure_route_inlines_the_pngs_as_data_uris()
    {
        var transport = new CannedTransport
        {
            Responder = request =>
            {
                var answer = request.Body!.Contains(BallonsQuestion, StringComparison.Ordinal) ? "[3]" : "[bicycle]";
                return CannedResponse.Json(200, Completion(answer));
            },
        };
        var api = new AzureAIModelApi(
            "gpt-4o",
            "https://example.com/models",
            settings: new AzureAIClientSettings
            {
                Transport = transport,
                TokenCredential = new FixedTokenCredential(),
                ConfigureClientOptions = options => options.Retry.MaxRetries = 0,
            });

        var log = await Run(new Model(api));

        Assert.Equal(2, transport.Requests.Count);
        foreach (var request in transport.Requests)
        {
            var messages = request.BodyJson["messages"]!.AsArray();
            Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
            var parts = messages[1]!["content"]!.AsArray();
            Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
            Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
            var url = parts[1]!["image_url"]!["url"]!.GetValue<string>();
            Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
            Assert.True(url.Length > 1000, "the PNG bytes were not inlined");
        }

        Assert.All(log.Samples!, sample => Assert.Equal("C", sample.Scores!["match"].Text));
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["images", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : images", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("dataset   : 2 samples", text);
        Assert.Contains("status    : success (2/2 samples completed)", text);
        Assert.Contains("match", text);
        Assert.Contains("accuracy", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static void AssertImageSample(Sample sample, string question, string file, Target target)
    {
        var message = Assert.IsType<ChatMessageUser>(Assert.Single(sample.Input.Messages!));
        Assert.Equal(2, message.ContentList.Count);
        Assert.Equal(question, Assert.IsType<ContentText>(message.ContentList[0]).Text);
        var image = Assert.IsType<ContentImage>(message.ContentList[1]);
        Assert.Equal(Path.GetFullPath(Path.Combine(DataDirectory, file)), image.Image);
        Assert.True(File.Exists(image.Image), $"{file} was not linked into the test output");
        Assert.Equal(target, sample.Target);
    }

    private async Task<EvalLog> Run(Model model)
    {
        var log = await Eval.RunAsync(
            ImagesExample.Build(DatasetPath),
            new EvalOptions
            {
                Model = model,
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
            },
            CancellationToken.None);

        Assert.True(log.Status == EvalStatus.Success, $"status {log.Status}: {log.Error?.Message ?? "(no error)"}");
        Assert.Equal(2, log.Results!.CompletedSamples);
        var score = Assert.Single(log.Results.Scores);
        Assert.Equal("match", score.Name);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);
        Assert.NotNull(log.Location);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
        return log;
    }

    /// <summary>A chat completion in the Azure AI Model Inference shape.</summary>
    private static string Completion(string content) =>
        new JsonObject
        {
            ["id"] = "cmpl-1",
            ["created"] = 123,
            ["model"] = "gpt-4o",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 300, ["completion_tokens"] = 3, ["total_tokens"] = 303 },
        }.ToJsonString();

    /// <summary>A token credential returning a fixed token (stands in for DefaultAzureCredential).</summary>
    private sealed class FixedTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
