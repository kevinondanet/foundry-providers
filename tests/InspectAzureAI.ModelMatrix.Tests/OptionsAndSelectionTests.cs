using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.SweShowcase;

namespace InspectAzureAI.ModelMatrix.Tests;

public class OptionsAndSelectionTests
{
    private static MatrixOptions Parse(params string[] args) => MatrixOptions.Parse(args.ToList());

    [Fact]
    public void defaults_are_claude_code_on_hello_swe_one_sample_sequentially()
    {
        var options = Parse();
        Assert.Equal("hello-swe", options.Task);
        Assert.Equal(AgentChoice.ClaudeCodeName, options.Agent);
        Assert.Equal(1, options.Limit);
        Assert.Equal(1, options.Parallel);
        Assert.Equal("docker", options.SandboxType);
        Assert.False(options.IncludeNonChat);
        Assert.Null(options.OutPath);
    }

    [Fact]
    public void fake_defaults_to_mini_swe_on_the_local_sandbox()
    {
        var options = Parse("--fake");
        Assert.Equal(AgentChoice.MiniSweName, options.Agent);
        Assert.Equal("local", options.SandboxType);
        Assert.True(options.Run.Fake);
    }

    [Fact]
    public void list_options_repeat_and_split_on_commas()
    {
        var options = Parse("--only", "a, b", "--only", "c", "--skip", "d", "--format", "OpenAI,Anthropic", "--parallel", "3", "--sample-id", "2", "--limit", "5", "--out", "m.json");
        Assert.Equal(["a", "b", "c"], options.Only);
        Assert.Equal(["d"], options.Skip);
        Assert.Equal(["OpenAI", "Anthropic"], options.Formats);
        Assert.Equal(3, options.Parallel);
        Assert.Null(options.Limit);   // --sample-id wins over --limit
        Assert.Equal("m.json", options.OutPath);
    }

    [Theory]
    [InlineData("--model", "gpt-4o")]
    [InlineData("--route", "anthropic")]
    [InlineData("--parallel", "0")]
    [InlineData("--bogus", "x")]
    public void rejected_arguments_are_usage_errors(string flag, string value)
    {
        Assert.Throws<UsageError>(() => Parse(flag, value));
    }

    [Fact]
    public void resume_takes_a_path_and_excludes_only()
    {
        Assert.Equal("m.json", Parse("--resume", "m.json").ResumePath);
        Assert.Throws<UsageError>(() => Parse("--resume", "m.json", "--only", "a"));
    }

    private static FoundryDeployment Deployment(string name, string format = "OpenAI", string state = "Succeeded", bool chat = true) =>
        new(name, name, format, "1", state, null, null, new Dictionary<string, string> { ["chatCompletion"] = chat ? "true" : "false" });

    [Fact]
    public void anthropic_format_takes_the_messages_route()
    {
        Assert.Equal("anthropic", DeploymentSelection.RouteFor(Deployment("claude-sonnet-4-6", "Anthropic")));
        Assert.Equal("anthropic", DeploymentSelection.RouteFor(Deployment("my-claude", "anthropic")));
        Assert.Equal("models", DeploymentSelection.RouteFor(Deployment("gpt-4o")));
        Assert.Equal("models", DeploymentSelection.RouteFor(Deployment("claude-lookalike", "OpenAI")));
    }

    [Fact]
    public void selection_applies_every_skip_rule_and_sorts_by_name()
    {
        var deployments = new[]
        {
            Deployment("zeta"),
            Deployment("alpha", "Anthropic"),
            Deployment("image", "Black Forest Labs", chat: false),
            Deployment("stale", state: "Failed"),
            Deployment("mistral", "Mistral AI"),
            Deployment("skipped"),
        };

        var all = DeploymentSelection.Select(deployments, Parse("--skip", "SKIPPED"));
        Assert.Equal(["alpha", "image", "mistral", "skipped", "stale", "zeta"], all.Select(s => s.Deployment.Name));
        Assert.Equal([null, "chatCompletion=false", null, "--skip", "provisioningState=Failed", null], all.Select(s => s.SkipReason));

        var withImages = DeploymentSelection.Select(deployments, Parse("--include-non-chat"));
        Assert.True(withImages.Single(s => s.Deployment.Name == "image").Selected);

        var onlyAnthropic = DeploymentSelection.Select(deployments, Parse("--format", "anthropic"));
        Assert.Equal(["alpha"], onlyAnthropic.Where(s => s.Selected).Select(s => s.Deployment.Name));

        var only = DeploymentSelection.Select(deployments, Parse("--only", "ZETA,nope,stale"));
        Assert.Equal(["stale", "zeta"], only.Select(s => s.Deployment.Name));   // deployments outside --only are not rows at all
        Assert.Equal(["zeta"], only.Where(s => s.Selected).Select(s => s.Deployment.Name));
        Assert.Equal(["nope"], DeploymentSelection.UnknownOnly(deployments, Parse("--only", "ZETA,nope")));
    }
}
