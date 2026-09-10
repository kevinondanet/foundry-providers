using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>The Codex CLI environment (<c>_codex_cli/codex_cli.py:346-351</c>, deviation D-X1).</summary>
public class CodexCliEnvTests
{
    [Fact]
    public void defaults_point_codex_at_the_bridge_in_python_order()
    {
        var env = CodexCliEnv.Build("/workspace/.codex", "http://127.0.0.1:4321", "tok-123", null);

        Assert.Equal(["CODEX_HOME", "OPENAI_API_KEY", "OPENAI_BASE_URL", "RUST_LOG"], env.Keys);
        Assert.Equal(["/workspace/.codex", "tok-123", "http://127.0.0.1:4321/v1", "warning"], env.Values);
    }

    [Fact]
    public void caller_env_wins_in_place_and_new_variables_are_appended()
    {
        var env = CodexCliEnv.Build(
            "/workspace/.codex",
            "http://127.0.0.1:4321",
            "tok-123",
            new Dictionary<string, string> { ["EXTRA"] = "1", ["RUST_LOG"] = "debug", ["OPENAI_API_KEY"] = "mine" });

        Assert.Equal(["CODEX_HOME", "OPENAI_API_KEY", "OPENAI_BASE_URL", "RUST_LOG", "EXTRA"], env.Keys);
        Assert.Equal("mine", env["OPENAI_API_KEY"]);
        Assert.Equal("debug", env["RUST_LOG"]);
        Assert.Equal("1", env["EXTRA"]);
    }
}
