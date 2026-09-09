using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.HveDemo.Fake;

/// <summary>
/// Wires the offline mode together, the counterpart of an example's <c>FakeSandbox</c> method: a
/// <see cref="FakeSandboxScript"/> whose rules answer the Copilot agent's discovery commands (<c>which copilot</c>,
/// <c>pwd</c>), hand the CLI launch to <see cref="FakeCopilotCli"/> (which talks to the real bridge), and run everything
/// else, the setup scripts, the check commands, the baseline's <c>bash</c> tool, for real on this host inside the sample's
/// mirror directory. Registering it gives the <c>fake</c> sandbox type of <see cref="ScriptedSandboxProvider"/>.
/// </summary>
public static class HveFake
{
    /// <summary>The version the solver asks for under <c>--fake</c>: the fake answers <c>which copilot</c>, so nothing is downloaded.</summary>
    public const string CopilotVersion = "sandbox";

    /// <summary>Builds the script and the fake CLI bound to it.</summary>
    public static FakeSandboxScript Script()
    {
        var script = new FakeSandboxScript { RunUnmatchedLocally = true };
        var cli = new FakeCopilotCli(() => ScriptedSandboxEnvironment.Current ?? script.Environments.LastOrDefault());
        script
            .OnExact(FakeSandboxScript.Ok(FakeCopilotCli.BinaryPath + "\n"), "bash", "-c", "which copilot")
            .OnExact(FakeSandboxScript.Ok(ScriptedSandboxEnvironment.WorkingDirectory + "\n"), "bash", "-c", "pwd")
            .OnPrefix(FakeSandboxScript.Ok("Linux\n"), "bash", "-c", "uname -s")
            .OnMatch(FakeCopilotCli.IsLaunch, cli.Run);
        return script;
    }

    /// <summary>Registers a fresh script and returns the sandbox spec selecting it (and the script, for assertions).</summary>
    public static (SandboxSpec Sandbox, FakeSandboxScript Script) Register()
    {
        var script = Script();
        return (ScriptedSandboxProvider.Register(script), script);
    }

    /// <summary>The <c>bash -c</c> argv shape shared with the agents' sandbox utilities, for tests that script more commands.</summary>
    public static IReadOnlyList<string> BashCommand(string cmd) => SandboxUtil.BashCommand(cmd);
}
