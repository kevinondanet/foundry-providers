using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

public class CompletionParamsTests
{
    private static readonly GenerateConfig FullConfig = new()
    {
        FrequencyPenalty = 0.1,
        PresencePenalty = 0.2,
        Temperature = 0.3,
        TopP = 0.4,
        MaxTokens = 5,
        StopSeqs = ["x"],
        Seed = 6,
        NumChoices = 3,
        Logprobs = true,
        TopLogprobs = 2,
        ParallelToolCalls = false,
        ReasoningEffort = "high",
    };

    [Fact]
    public void completion_params_field_mapping()
    {
        var parameters = Fixtures.Api("Llama-3.3-70B-Instruct").CompletionParams(FullConfig);
        Assert.Equal(
            """{"frequency_penalty":0.1,"presence_penalty":0.2,"temperature":0.3,"top_p":0.4,"max_tokens":5,"stop":["x"],"seed":6}""",
            parameters.ToJsonString());
    }

    [Theory]
    [InlineData("o3-mini")]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    public void completion_params_use_max_completion_tokens_for_gpt5_and_o_series(string model)
    {
        var parameters = Fixtures.Api(model).CompletionParams(new GenerateConfig { MaxTokens = 5 });
        Assert.Equal("""{"max_completion_tokens":5}""", parameters.ToJsonString());
    }

    [Fact]
    public void completion_params_empty_when_config_empty() =>
        Assert.Equal("{}", Fixtures.Api().CompletionParams(new GenerateConfig()).ToJsonString());

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("o1-preview", true)]
    [InlineData("o3-mini", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("Llama-3.3-70B-Instruct", false)]
    [InlineData("phi-4-o2", true)]
    public void needs_max_completion_tokens(string model, bool expected) =>
        Assert.Equal(expected, OpenAIUtil.NeedsMaxCompletionTokens(model));
}
