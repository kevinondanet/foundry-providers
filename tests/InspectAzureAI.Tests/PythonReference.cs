using System.Diagnostics;

namespace InspectAzureAI.Tests;

/// <summary>
/// Runs a snippet under the inspect_ai Python venv to compute reference values (JSON schemas, tool info dumps).
/// The interpreter is <c>INSPECT_AI_PYTHON</c>, else the checkout's <c>.venv</c>; tests marked
/// <see cref="PythonFactAttribute"/> skip when neither exists.
/// </summary>
internal static class PythonReference
{
    private const string DefaultPython = "/Users/kevinburrowes/Documents/code/inspect_ai/.venv/bin/python";

    public static string? Interpreter
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("INSPECT_AI_PYTHON");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            {
                return configured;
            }

            return File.Exists(DefaultPython) ? DefaultPython : null;
        }
    }

    public static bool Available => Interpreter is not null;

    /// <summary>Regenerates provider wire requests against Python using in-memory HTTP transports only.</summary>
    public static void GenerateProviderFixtures(string destination)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "generate-provider-goldens.py");
        Run($"import os, runpy\nos.environ['INSPECT_PROVIDER_FIXTURE_DIR'] = {System.Text.Json.JsonSerializer.Serialize(destination)}\nrunpy.run_path({System.Text.Json.JsonSerializer.Serialize(script)}, run_name='__main__')");
    }

    /// <summary>Runs <paramref name="script"/> and returns its trimmed stdout; a non-zero exit is an <see cref="InvalidOperationException"/> carrying stderr.</summary>
    public static string Run(string script)
    {
        var interpreter = Interpreter ?? throw new InvalidOperationException("No inspect_ai Python interpreter is available.");
        var start = new ProcessStartInfo(interpreter, "-")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python could not be started.");
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"python exited with {process.ExitCode}:\n{stderr}");
        }

        return stdout.Trim();
    }
}

/// <summary>A fact that runs only when the inspect_ai Python venv is available for cross-checking.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (!PythonReference.Available)
        {
            Skip = "Set INSPECT_AI_PYTHON to the inspect_ai venv interpreter to run Python cross-checks.";
        }
    }
}
