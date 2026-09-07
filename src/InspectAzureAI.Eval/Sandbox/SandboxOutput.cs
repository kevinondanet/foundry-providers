namespace InspectAzureAI.Eval.Sandbox;

/// <summary>The stdout/stderr join the sandboxes report on a timeout (Python's <c>TimeoutError</c> output): stdout, then stderr, separated by a newline only when both are present.</summary>
internal static class SandboxOutput
{
    public static string Combine(string stdout, string stderr) =>
        stderr.Length == 0 ? stdout : stdout.Length == 0 ? stderr : stdout + "\n" + stderr;
}
