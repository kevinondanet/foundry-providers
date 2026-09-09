using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Tests.Bridge;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Shared plumbing of the bridge example tests: fake contexts, temp log dirs and an offline <c>Eval.RunAsync</c>.</summary>
internal static class BridgeTestSupport
{
    /// <summary>A <c>--fake</c> context for <paramref name="exampleName"/> ("bridge/langchain"), pointing at the data files linked next to the test assembly.</summary>
    public static ExampleContext FakeContext(string exampleName, IReadOnlyDictionary<string, string>? taskArgs = null) =>
        new(
            Path.Combine(AppContext.BaseDirectory, exampleName.Replace('/', Path.DirectorySeparatorChar)),
            null,
            true,
            taskArgs ?? new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            null,
            TextWriter.Null);

    /// <summary>A live-looking context (not fake) with the given <c>-T</c> args, for the tool-selection tests.</summary>
    public static ExampleContext LiveContext(string exampleName, IReadOnlyDictionary<string, string>? taskArgs = null) =>
        FakeContext(exampleName, taskArgs) with { Fake = false, Model = "deployment" };

    public static string NewLogDir(string prefix) => Path.Combine(Path.GetTempPath(), "inspect-examples-tests", prefix + "-" + Guid.NewGuid().ToString("N"));

    public static void DeleteLogDir(string logDir)
    {
        try
        {
            Directory.Delete(logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs <paramref name="task"/> against <paramref name="model"/> offline, writing a JSON log to <paramref name="logDir"/>.</summary>
    public static Task<EvalLog> RunAsync(EvalTask task, Model model, string logDir) =>
        Eval.RunAsync(task, new EvalOptions { Model = model, LogDir = logDir, LogFormat = LogFormat.Json });

    /// <summary>The scripted api behind a fake model.</summary>
    public static ScriptedModelApi Scripted(Model model) => Assert.IsType<ScriptedModelApi>(model.Api);
}
