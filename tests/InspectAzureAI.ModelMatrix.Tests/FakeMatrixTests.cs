using System.Text.Json;

namespace InspectAzureAI.ModelMatrix.Tests;

/// <summary>The whole app offline: the scripted model on the fake catalog, the local sandbox, JSON and Markdown out.</summary>
public class FakeMatrixTests
{
    [Fact]
    public async Task fake_matrix_runs_the_chat_deployments_and_skips_the_rest()
    {
        var dir = Directory.CreateTempSubdirectory("model-matrix-");
        try
        {
            var output = new StringWriter();
            var outPath = Path.Combine(dir.FullName, "matrix.json");
            var markdownPath = Path.Combine(dir.FullName, "matrix.md");
            var exit = await MatrixCli.RunAsync(
                ["--fake", "--task", "hello-swe", "--log-dir", Path.Combine(dir.FullName, "logs"), "--out", outPath, "--markdown", markdownPath],
                output,
                CancellationToken.None);

            var console = output.ToString();
            Assert.True(exit == 0, console);
            Assert.Contains("agent    : mini-swe", console);
            Assert.Contains("models   : 4 discovered, 2 selected, 2 skipped", console);
            Assert.Contains("fake-image: skipped (chatCompletion=false)", console);
            Assert.Contains("fake-stale: skipped (provisioningState=Failed)", console);
            Assert.Contains("2 run (2 ok, 0 partial, 0 incorrect, 0 unscored, 0 errored), 2 skipped", console);

            var report = MatrixReport.FromJson(File.ReadAllText(outPath));
            Assert.Equal("fake://scripted", report.Endpoint);
            Assert.Equal("exec_check", report.Scorer);
            var claude = report.Rows.Single(r => r.Deployment == "fake-claude");
            Assert.Equal("anthropic", claude.Route);
            Assert.Equal(MatrixRow.Ok, claude.Status);
            Assert.Equal(1, claude.Accuracy);
            Assert.Single(claude.Samples);
            Assert.Equal("exec_check=C", claude.Samples[0].Score);
            Assert.True(File.Exists(claude.Log), claude.Log);
            Assert.Equal(JsonValueKind.True, ((JsonElement)report.Config["fake"]!).ValueKind);
            Assert.Contains("| fake-gpt | OpenAI | models | ok | 1.000 |", File.ReadAllText(markdownPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task fake_refuses_claude_code_with_a_usage_error()
    {
        var output = new StringWriter();
        var exit = await MatrixCli.RunAsync(["--fake", "--agent", "claude-code"], output, CancellationToken.None);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task resume_reruns_only_the_errored_rows_and_merges()
    {
        var dir = Directory.CreateTempSubdirectory("model-matrix-resume-");
        try
        {
            var previousPath = Path.Combine(dir.FullName, "previous.json");
            var previous = new MatrixReport
            {
                Endpoint = "fake://scripted",
                Task = "hello-swe",
                Scorer = "exec_check",
                Agent = "mini-swe",
                Sandbox = "local",
                Config = new Dictionary<string, object?>(),
                Rows =
                [
                    new MatrixRow { Deployment = "fake-claude", Model = "claude-fake", Format = "Anthropic", Route = "anthropic", Status = MatrixRow.Ok, Accuracy = 1, Tokens = 5, Seconds = 1 },
                    new MatrixRow { Deployment = "fake-gpt", Model = "gpt-fake", Format = "OpenAI", Route = "models", Status = MatrixRow.Error, ErrorMessage = "network down", Seconds = 1 },
                    new MatrixRow { Deployment = "fake-gone", Model = "x", Format = "OpenAI", Route = "models", Status = MatrixRow.Error, ErrorMessage = "no longer deployed", Seconds = 1 },
                ],
            };
            File.WriteAllText(previousPath, previous.ToJson());

            var output = new StringWriter();
            var outPath = Path.Combine(dir.FullName, "matrix.json");
            var exit = await MatrixCli.RunAsync(["--fake", "--resume", previousPath, "--log-dir", Path.Combine(dir.FullName, "logs"), "--out", outPath], output, CancellationToken.None);
            var console = output.ToString();
            Assert.Contains("Resuming 2 errored deployment(s)", console);
            Assert.Contains("warning: --only fake-gone matches no deployment", console);
            Assert.DoesNotContain("fake-claude: started", console);

            var report = MatrixReport.FromJson(File.ReadAllText(outPath));
            Assert.Equal(["fake-claude", "fake-gone", "fake-gpt"], report.Rows.Select(r => r.Deployment));
            Assert.Equal(MatrixRow.Ok, report.Rows.Single(r => r.Deployment == "fake-gpt").Status);
            Assert.Equal(5, report.Rows.Single(r => r.Deployment == "fake-claude").Tokens);   // carried over untouched
            Assert.Equal(MatrixRow.Error, report.Rows.Single(r => r.Deployment == "fake-gone").Status);   // still errored, still listed
            Assert.Equal(1, exit);
            Assert.Contains("resumedFrom", File.ReadAllText(outPath));

            var mismatch = await MatrixCli.RunAsync(["--fake", "--task", "pytest-fix", "--resume", previousPath, "--log-dir", Path.Combine(dir.FullName, "logs")], new StringWriter(), CancellationToken.None);
            Assert.Equal(2, mismatch);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task show_prints_a_saved_matrix_without_running()
    {
        var dir = Directory.CreateTempSubdirectory("model-matrix-show-");
        try
        {
            var path = Path.Combine(dir.FullName, "m.json");
            File.WriteAllText(path, new MatrixReport
            {
                Endpoint = "fake://scripted", Task = "hello-swe", Scorer = "exec_check", Agent = "mini-swe", Sandbox = "local",
                Config = new Dictionary<string, object?>(),
                Rows = [new MatrixRow { Deployment = "d", Model = "d", Format = "OpenAI", Route = "models", Status = MatrixRow.Ok, Accuracy = 1, Tokens = 3, Seconds = 1 }],
            }.ToJson());

            var output = new StringWriter();
            var markdown = Path.Combine(dir.FullName, "m.md");
            var exit = await MatrixCli.RunAsync(["--show", path, "--markdown", markdown], output, CancellationToken.None);
            Assert.Equal(0, exit);
            Assert.Contains("1 run (1 ok,", output.ToString());
            Assert.DoesNotContain("started", output.ToString());
            Assert.Contains("| d | OpenAI | models | ok | 1.000 | 3 |", File.ReadAllText(markdown));
            Assert.Equal(2, await MatrixCli.RunAsync(["--show", Path.Combine(dir.FullName, "missing.json")], new StringWriter(), CancellationToken.None));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
