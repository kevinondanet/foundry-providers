using InspectAzureAI.Provider.Foundry;

namespace InspectAzureAI.ModelMatrix.Tests;

public class ReportTests
{
    private static SelectedDeployment Entry(string name, string format, string? skip = null) =>
        new(new FoundryDeployment(name, name, format, "1", "Succeeded", null, null, new Dictionary<string, string>()), DeploymentSelection.RouteFor(new FoundryDeployment(name, name, format, "1", "Succeeded", null, null, new Dictionary<string, string>())), skip);

    private static MatrixReport Report() => new()
    {
        Resource = new MatrixResource("res", "rg", "eastus2", "AIServices"),
        Endpoint = "https://res.services.ai.azure.com/models",
        Task = "hello-swe",
        Scorer = "exec_check",
        Agent = "claude-code",
        Sandbox = "docker",
        Config = new Dictionary<string, object?> { ["limit"] = 1 },
        Rows =
        [
            new MatrixRow { Deployment = "gpt-4o", Model = "gpt-4o", Format = "OpenAI", Route = "models", Status = MatrixRow.Ok, Accuracy = 1, Tokens = 1234, Seconds = 12.34 },
            new MatrixRow { Deployment = "claude", Model = "claude", Format = "Anthropic", Route = "anthropic", Status = MatrixRow.Incorrect, Accuracy = 0, Tokens = 99, Seconds = 3 },
            MatrixRow.Failed(Entry("broken", "xAI"), "Azure request failed (HTTP 400): bad | pipe\nsecond line", 1.5),
            MatrixRow.SkippedRow(Entry("flux", "Black Forest Labs", "chatCompletion=false")),
        ],
    };

    [Fact]
    public void table_has_one_line_per_deployment_and_a_summary()
    {
        var table = Report().RenderTable();
        var lines = table.TrimEnd().Split('\n');
        Assert.StartsWith("deployment", lines[0]);
        Assert.Contains("gpt-4o", lines[1]);
        Assert.Contains("1.000", lines[1]);
        Assert.Contains("1234", lines[1]);
        Assert.Contains("error", lines[3]);
        Assert.Contains("bad | pipe", lines[3]);
        Assert.DoesNotContain("second line", table);
        Assert.Contains("chatCompletion=false", lines[4]);
        Assert.Equal("3 run (1 ok, 0 partial, 1 incorrect, 0 unscored, 1 errored), 1 skipped; 1333 tokens", lines[^1]);
    }

    [Fact]
    public void markdown_escapes_pipes_and_right_aligns_numbers()
    {
        var markdown = Report().RenderMarkdown();
        Assert.Contains("| deployment | format | route | status | accuracy | tokens | time | note |", markdown);
        Assert.Contains("|---|---|---|---|---:|---:|---:|---|", markdown);
        Assert.Contains("bad \\| pipe", markdown);
        Assert.Contains("`res` (rg, eastus2)", markdown);
    }

    [Fact]
    public void json_round_trips_rows_and_summary()
    {
        var report = Report();
        var json = report.ToJson();
        Assert.Contains("\"capturedAt\"", json);
        Assert.DoesNotContain("\"skipReason\": null", json);

        var back = MatrixReport.FromJson(json);
        Assert.Equal(4, back.Rows.Count);
        Assert.Equal(report.Summary, back.Summary);
        Assert.True(back.HasErrors);
        Assert.Equal("Azure request failed (HTTP 400): bad | pipe\nsecond line", back.Rows[2].ErrorMessage);
    }

    [Fact]
    public void merge_replaces_rerun_rows_and_keeps_the_rest_sorted()
    {
        var previous = Report();
        var rerun = previous with
        {
            Rows = [new MatrixRow { Deployment = "BROKEN", Model = "broken", Format = "xAI", Route = "models", Status = MatrixRow.Ok, Accuracy = 1, Tokens = 10, Seconds = 2 }],
        };

        var merged = rerun.MergedInto(previous);
        Assert.Equal(["BROKEN", "claude", "flux", "gpt-4o"], merged.Rows.Select(r => r.Deployment));
        Assert.Equal(MatrixRow.Ok, merged.Rows[0].Status);
        Assert.False(merged.HasErrors);
        Assert.Equal(1343, merged.Summary.Tokens);
    }

    [Fact]
    public void a_hit_sample_limit_shows_in_the_note()
    {
        var row = new MatrixRow
        {
            Deployment = "slow", Model = "slow", Format = "xAI", Route = "models", Status = MatrixRow.Incorrect, Accuracy = 0,
            Samples = [new MatrixSampleResult(1, 1, "exec_check=I", 0, 1200.8, null, "time")],
        };
        Assert.Equal("limit=time", row.Note);
        Assert.Equal("", (row with { Samples = [] }).Note);
    }
}
