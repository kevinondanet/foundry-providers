using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Models;

/// <summary>
/// Port of <c>model/_providers/mockllm.py</c> <c>MockLLM</c>: every generate returns
/// <see cref="DefaultOutput"/> (model <c>mockllm</c>), so an eval can run without any service. The
/// <c>custom_outputs</c> model arg (<c>-M custom_outputs=[a,b]</c>) scripts the texts returned in order — Python takes
/// <c>ModelOutput</c> objects, which a command line cannot carry, so this port takes strings — and raises once they are
/// exhausted, as Python's iterator does.
/// </summary>
public sealed class MockLlmModelApi : IModelApi
{
    /// <summary>The text Python's mock returns.</summary>
    public const string DefaultOutput = "Default output from mockllm/model";

    private readonly Queue<string>? _outputs;

    private readonly object _sync = new();

    public MockLlmModelApi(string modelName = "mockllm/model", IReadOnlyDictionary<string, object?>? modelArgs = null)
    {
        ModelName = modelName;
        if (modelArgs is not null && modelArgs.TryGetValue("custom_outputs", out var custom))
        {
            _outputs = custom switch
            {
                string text => new Queue<string>([text]),
                IEnumerable<object?> items when items.All(item => item is string) => new Queue<string>(items.Cast<string>()),
                _ => throw new PrerequisiteError("model_args['custom_outputs'] must be a string or a list of strings (this port scripts the returned texts)."),
            };
        }
    }

    public string ModelName { get; }

    public int? MaxTokens() => null;

    public Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        string text;
        if (_outputs is null)
        {
            text = DefaultOutput;
        }
        else
        {
            lock (_sync)
            {
                if (!_outputs.TryDequeue(out var next))
                {
                    throw new InvalidOperationException("mockllm: custom_outputs is exhausted (no output left for this generate call).");
                }

                text = next;
            }
        }

        var request = new JsonObject
        {
            ["model"] = ModelName,
            ["messages"] = input.Count,
            ["tools"] = tools.Count,
        };
        var call = ModelCall.Create(request);
        var output = ModelOutput.FromContent("mockllm", text);
        call.SetResponse(new JsonObject { ["output"] = text });
        return Task.FromResult(new GenerateResult(output, null, call));
    }
}
