using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Analysis;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// The tabular analysis port: evals, samples, messages and events tables over the inspect_ai analysis fixture logs
/// (copied from <c>tests/analysis/test_logs</c>), the prepare operations, the Table writers, and cross-checks
/// against the venv's <c>evals_df</c> / <c>samples_df</c> (gated on <c>INSPECT_PY</c>).
/// </summary>
public class AnalysisTests
{
    private static readonly string LogsDir = FixtureRoot("eval-logs", "analysis");
    private static readonly string BrowserLog = Path.Combine(LogsDir, "2025-05-12T20-27-36-04-00_browser.json");
    private static readonly string SecurityGuideLog = Path.Combine(LogsDir, "2025-05-12T20-28-26-04-00_security-guide.json");
    private static readonly string PopularityLog = Path.Combine(LogsDir, "2025-05-12T20-28-13-04-00_popularity.json");
    private static readonly string ChoicesDir = FixtureRoot("eval-logs", "analysis-choices");
    private static readonly string OldChoicesLog = Path.Combine(ChoicesDir, "mmlu-no-summary-choices.eval");
    private static readonly string NewChoicesLog = Path.Combine(ChoicesDir, "mmlu-summary-choices.eval");

    // the default column sets of the venv (evals_df / samples_df / messages_df / events_df over test_logs)
    private static readonly string[] EvalsDefaultColumns =
    [
        "eval_id", "eval_set_id", "run_id", "task_id", "log", "created", "tags", "git_origin", "git_commit", "packages", "metadata",
        "task_name", "task_display_name", "task_version", "task_file", "task_attribs", "solver", "solver_args", "sandbox_type", "sandbox_config",
        "model", "model_base_url", "model_args", "model_generate_config", "model_roles",
        "dataset_name", "dataset_location", "dataset_samples", "dataset_sample_ids", "dataset_shuffled",
        "epochs", "epochs_reducer", "approval", "message_limit", "token_limit", "token_limit_type", "turn_limit", "time_limit", "working_limit",
        "status", "error_message", "error_traceback", "total_samples", "completed_samples",
        "score_headline_name", "score_headline_score", "score_headline_metric", "score_headline_value", "score_headline_stderr",
        "score_includes_accuracy", "score_includes_stderr", "score_match_accuracy", "score_match_stderr", "score_model_graded_fact_accuracy", "score_model_graded_fact_stderr",
    ];

    private static readonly string[] SamplesDefaultColumns =
    [
        "sample_id", "eval_id", "log", "id", "epoch", "input", "choices", "target", "metadata_label_confidence", "metadata_nested",
        "score_includes", "score_match", "score_model_graded_fact", "model_usage", "total_tokens", "total_time", "working_time",
        "message_count", "turn_count", "token_limit_usage", "error", "limit", "limit_reason", "retries", "fallbacks",
    ];

    private static readonly string[] MessagesDefaultColumns =
    [
        "message_id", "sample_id", "eval_id", "log", "role", "source", "content", "tool_calls", "tool_call_id", "tool_call_function", "tool_call_error", "order",
    ];

    private static readonly string[] EventsDefaultColumns = ["event_id", "sample_id", "eval_id", "log", "event", "span_id", "order"];

    // ---------------------------------------------------------------- evals

    [Fact]
    public void evals_table_has_pythons_default_columns_and_one_row_per_log()
    {
        var table = EvalsTable.Read(LogsDir);
        Assert.Equal(4, table.RowCount);
        Assert.Equal(EvalsDefaultColumns, table.Columns);
    }

    [Fact]
    public void evals_table_values_match_the_venv_for_the_browser_log()
    {
        var table = EvalsTable.Read(BrowserLog);
        Assert.Equal(1, table.RowCount);
        Assert.Equal("ZB2vu5GNujYaBdPSReCeop", table[0, "eval_id"]);
        Assert.Equal("Ta3ZFVTCTcjsb3AEX9FD9v", table[0, "run_id"]);
        Assert.Equal("XSogYfcZrx876NZYKz3Xf5", table[0, "task_id"]);
        Assert.Equal(Path.GetFullPath(BrowserLog), table[0, "log"]);
        Assert.Equal(new DateTimeOffset(2025, 5, 13, 0, 27, 36, TimeSpan.Zero), table[0, "created"]);
        Assert.Equal("", table[0, "tags"]);
        Assert.Equal("git@github.com:UKGovernmentBEIS/inspect_ai.git", table[0, "git_origin"]);
        Assert.Equal("e9a9d7161", table[0, "git_commit"]);
        Assert.Equal("{\"inspect_ai\": \"0.3.95.dev16+g6527d0861\"}", table[0, "packages"]);
        Assert.Equal("{}", table[0, "metadata"]);
        Assert.Equal("browser", table[0, "task_name"]);
        Assert.Equal("browser", table[0, "task_display_name"]);
        Assert.Equal(0L, table[0, "task_version"]);
        Assert.Equal("examples/browser/browser.py", table[0, "task_file"]);
        Assert.Equal("{}", table[0, "task_attribs"]);
        Assert.Null(table[0, "solver"]);
        Assert.Equal("docker", table[0, "sandbox_type"]);
        Assert.Equal("compose.yaml", table[0, "sandbox_config"]);
        Assert.Equal("openai/gpt-4o-mini", table[0, "model"]);
        Assert.Equal("{}", table[0, "model_generate_config"]);
        Assert.Equal(1L, table[0, "dataset_samples"]);
        Assert.Equal("[1]", table[0, "dataset_sample_ids"]);
        Assert.Equal(false, table[0, "dataset_shuffled"]);
        Assert.Equal(1L, table[0, "epochs"]);
        Assert.Equal("[\"mean\"]", table[0, "epochs_reducer"]);
        Assert.Null(table[0, "message_limit"]);
        Assert.Equal("success", table[0, "status"]);
        Assert.Null(table[0, "error_message"]);
        Assert.Equal(1L, table[0, "total_samples"]);
        Assert.Equal(1L, table[0, "completed_samples"]);
        Assert.Equal("includes", table[0, "score_headline_name"]);
        Assert.Equal("includes", table[0, "score_headline_score"]);
        Assert.Equal("accuracy", table[0, "score_headline_metric"]);
        Assert.Equal(1.0, table[0, "score_headline_value"]);
        Assert.Equal(0.0, table[0, "score_headline_stderr"]);
        Assert.Equal(1.0, table[0, "score_includes_accuracy"]);
        Assert.Equal(0.0, table[0, "score_includes_stderr"]);
        Assert.False(table.HasColumn("score_match_accuracy"));
    }

    [Fact]
    public void evals_table_column_groups_compose_like_python()
    {
        IReadOnlyList<Column> columns = [.. EvalColumns.Info, .. EvalColumns.Model, .. EvalColumns.Results, .. EvalColumns.Task];
        var table = EvalsTable.Read(LogsDir, columns);
        // eval_id is injected; log is already in EvalInfo; task_arg_* expands to nothing (no task args)
        Assert.Equal(1 + EvalColumns.Info.Count + EvalColumns.Model.Count + EvalColumns.Results.Count + EvalColumns.Task.Count - 1, table.ColumnCount);
        Assert.Equal("eval_id", table.Columns[0]);
        Assert.Contains("task_display_name", table.Columns);
    }

    [Fact]
    public void evals_table_configuration_group_includes_token_limit_type()
    {
        var table = EvalsTable.Read(LogsDir, EvalColumns.Configuration);
        Assert.Contains("token_limit", table.Columns);
        Assert.Contains("token_limit_type", table.Columns);
    }

    [Fact]
    public void evals_table_non_strict_read_reports_no_errors_for_the_fixtures()
    {
        var import = EvalsTable.ReadWithErrors(LogsDir);
        Assert.Equal(4, import.Table.RowCount);
        Assert.Empty(import.Errors);
    }

    [Fact]
    public void evals_table_accepts_list_eval_logs_filters()
    {
        var success = EvalLogFiles.ListEvalLogs(LogsDir, filter: log => log.Status == EvalStatus.Success);
        Assert.Equal(2, EvalsTable.Read(success.ToArray()).RowCount);
        var popularity = EvalLogFiles.ListEvalLogs(LogsDir, filter: log => log.Eval.Task == "popularity");
        Assert.Equal(1, EvalsTable.Read(popularity.ToArray()).RowCount);
    }

    [Fact]
    public void evals_table_reads_in_memory_logs()
    {
        var log = EvalLogFiles.ReadEvalLog(SecurityGuideLog);
        Assert.Equal(1, EvalsTable.Read(log).RowCount);
        var logs = EvalLogFiles.ListEvalLogs(LogsDir).Select(info => EvalLogFiles.ReadEvalLog(info)).ToArray();
        Assert.Equal(4, EvalsTable.Read(logs).RowCount);
    }

    [Fact]
    public void evals_table_disambiguates_score_columns_by_reducer_only_on_collision()
    {
        var log = ScoredLog(["mean", "max"], new EvalScore("match", "match") { Reducer = "mean", Metrics = Metrics(("accuracy", 0.5)) }, new EvalScore("match", "match") { Reducer = "max", Metrics = Metrics(("accuracy", 0.9)) });
        var table = EvalsTable.Read(log);
        Assert.Equal(0.5, table[0, "score_match_mean_accuracy"]);
        Assert.Equal(0.9, table[0, "score_match_max_accuracy"]);
        Assert.False(table.HasColumn("score_match_accuracy"));

        var single = ScoredLog(["mean"], new EvalScore("match", "match") { Reducer = "mean", Metrics = Metrics(("accuracy", 0.75)) });
        table = EvalsTable.Read(single);
        Assert.Equal(0.75, table[0, "score_match_accuracy"]);
        Assert.False(table.HasColumn("score_match_mean_accuracy"));

        var mixed = ScoredLog(null, new EvalScore("match", "match") { Reducer = "mean", Metrics = Metrics(("accuracy", 0.5)) }, new EvalScore("match", "match") { Metrics = Metrics(("C", 0.5)) });
        table = EvalsTable.Read(mixed);
        Assert.Equal(0.5, table[0, "score_match_accuracy"]);
        Assert.Equal(0.5, table[0, "score_match_C"]);
        Assert.False(table.HasColumn("score_match_mean_accuracy"));

        var collision = ScoredLog(null, new EvalScore("match", "match") { Reducer = "mean", Metrics = Metrics(("accuracy", 0.5)) }, new EvalScore("match", "match") { Metrics = Metrics(("accuracy", 0.75)) });
        table = EvalsTable.Read(collision);
        Assert.Equal(0.5, table[0, "score_match_mean_accuracy"]);
        Assert.Equal(0.75, table[0, "score_match_accuracy"]);
    }

    [Fact]
    public void evals_table_headline_metric_uses_the_metric_key()
    {
        var log = ScoredLog(null, new EvalScore("one", "dict_scorer")
        {
            Metrics = new Dictionary<string, EvalMetric>
            {
                ["nested_dict_metric_key1"] = new EvalMetric("key1", 0.25) { Group = "nested_dict_metric" },
                ["nested_dict_metric_key2"] = new EvalMetric("key2", 0.75) { Group = "nested_dict_metric" },
            },
        });
        var table = EvalsTable.Read(log);
        Assert.Equal("dict_scorer", table[0, "score_headline_name"]);
        Assert.Equal("one", table[0, "score_headline_score"]);
        Assert.Equal("nested_dict_metric_key1", table[0, "score_headline_metric"]);
        Assert.Equal(0.25, table[0, "score_headline_value"]);
        Assert.Equal(0.25, table[0, "score_one_nested_dict_metric_key1"]);
    }

    [Fact]
    public void evals_table_in_memory_log_without_location_uses_the_current_directory_like_python()
    {
        var table = EvalsTable.Read(ScoredLog(null));
        Assert.Equal(Directory.GetCurrentDirectory(), table[0, "log"]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), table[0, "created"]);
    }

    [Fact]
    public void evals_table_reflects_edited_tags_and_metadata()
    {
        using var temp = new TempDir();
        var log = EvalLogFiles.ReadEvalLog(BrowserLog) with { Eval = EvalLogFiles.ReadEvalLog(BrowserLog).Eval with { Tags = ["original"], Metadata = new Dictionary<string, object?> { ["key"] = "original" } } };
        log = EvalLogEditing.EditEvalLog(log, [new TagsEdit { TagsAdd = ["added"], TagsRemove = ["original"] }, new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["key"] = "edited" } }], new ProvenanceData("test"));
        var path = Path.Combine(temp.Path, Path.GetFileName(BrowserLog));
        EvalLogWriter.Write(log, path);

        var table = EvalsTable.Read(temp.Path);
        Assert.Equal("added", table[0, "tags"]);
        Assert.Equal("{\"key\": \"edited\"}", table[0, "metadata"]);
    }

    // ---------------------------------------------------------------- samples

    [Fact]
    public void samples_table_has_pythons_default_columns_and_one_row_per_sample()
    {
        var table = SamplesTable.Read(LogsDir);
        Assert.Equal(7, table.RowCount);
        Assert.Equal(SamplesDefaultColumns, table.Columns);
        string[] expectedIds = ["76snEZzrGrPY97wTmfrn3j", "ATYFNjyWUz4mZ5Dgj6yd4f", "G7qmTyE6WB9wLq6GAv5w9K", "HsRaLUYeLvb6ehcfhHorXD", "W8MPQk6wsigrQepwJF9pUJ", "azKp2SRnKjCTS9rimuwWy2", "jejv2PukU7Xq5AJrutaZi7"];
        Assert.Equal(expectedIds, table.Column("sample_id").Select(cell => (string)cell!).Order(StringComparer.Ordinal));
        var counts = table.Column("message_count").Select(cell => (long)cell!).ToList();
        Assert.All(counts, count => Assert.True(count >= 0));
        Assert.Contains(10L, counts);
    }

    [Fact]
    public void samples_table_values_match_the_venv_for_the_browser_log()
    {
        var table = SamplesTable.Read(BrowserLog);
        Assert.Equal(1, table.RowCount);
        Assert.Equal("ATYFNjyWUz4mZ5Dgj6yd4f", table[0, "sample_id"]);
        Assert.Equal("ZB2vu5GNujYaBdPSReCeop", table[0, "eval_id"]);
        Assert.Equal("1", table[0, "id"]);
        Assert.Equal(1L, table[0, "epoch"]);
        Assert.Equal("user:\nUse the web browser tool to navigate to https://www.aisi.gov.uk/. Then, see if you can find a page on the site that describes the work of the UK AISI. Then, summarize this work in two paragraphs.\n", table[0, "input"]);
        Assert.Null(table[0, "choices"]);
        Assert.Equal("", table[0, "target"]);
        Assert.Equal("C", table[0, "score_includes"]);
        Assert.Equal("{\"openai/gpt-4o-mini\": {\"input_tokens\": 15235, \"output_tokens\": 281, \"total_tokens\": 15516, \"input_tokens_cache_read\": 9088, \"reasoning_tokens\": 0}}", table[0, "model_usage"]);
        Assert.Equal(15516L, table[0, "total_tokens"]);
        Assert.Equal(18.517, table[0, "total_time"]);
        Assert.Equal(18.36, table[0, "working_time"]);
        Assert.Equal(10L, table[0, "message_count"]);
        Assert.Null(table[0, "turn_count"]);
        Assert.Null(table[0, "token_limit_usage"]);
        Assert.Equal("", table[0, "error"]);
        Assert.Null(table[0, "limit"]);
        Assert.Equal(0L, table[0, "retries"]);
        Assert.Equal(0L, table[0, "fallbacks"]);
    }

    [Fact]
    public void samples_table_reads_in_memory_logs_and_full_columns()
    {
        var log = EvalLogFiles.ReadEvalLog(SecurityGuideLog);
        Assert.Equal(3, SamplesTable.Read(log).RowCount);
        var logs = EvalLogFiles.ListEvalLogs(LogsDir).Select(info => EvalLogFiles.ReadEvalLog(info)).ToArray();
        Assert.Equal(7, SamplesTable.Read(logs).RowCount);

        IReadOnlyList<Column> columns = [.. SampleColumns.Summary, .. SampleColumns.Scores, .. SampleColumns.Messages];
        var table = SamplesTable.Read(LogsDir, columns);
        Assert.Equal(7, table.RowCount);
        Assert.Contains("messages", table.Columns);
        Assert.Contains("score_includes", table.Columns);
        Assert.Contains("score_includes_explanation", table.Columns);
        Assert.All(table.Column("messages"), cell => Assert.Matches("^(system|user):\n", (string)cell!));
    }

    [Fact]
    public void samples_table_later_column_definition_wins()
    {
        // "metadata_*" and "metadata" are different names, so both are read (as in Python: duplicates are resolved by name)
        IReadOnlyList<Column> columns = [.. SampleColumns.Summary, new SampleColumn("metadata", "metadata")];
        var table = SamplesTable.Read(PopularityLog, columns);
        Assert.True(table.HasColumn("metadata_label_confidence"));
        Assert.Contains("\"label_confidence\": ", (string)table[0, "metadata"]!, StringComparison.Ordinal);
        Assert.Contains("\"nested\": {", (string)table[0, "metadata"]!, StringComparison.Ordinal);

        // a repeated name keeps the later definition, in its later position
        IReadOnlyList<Column> overridden = [.. SampleColumns.Summary, new SampleColumn("id", "epoch", type: ColumnType.String)];
        var replaced = SamplesTable.Read(PopularityLog, overridden);
        Assert.Equal(1, replaced.Columns.Count(name => name == "id"));
        Assert.Equal(Table.FormatCell(replaced[0, "epoch"]), replaced[0, "id"]);
        Assert.True(replaced.IndexOf("id") > replaced.IndexOf("fallbacks"));
    }

    [Fact]
    public void samples_table_suffixes_columns_that_collide_across_evals_and_samples()
    {
        IReadOnlyList<Column> columns = [.. EvalColumns.Info, .. SampleColumns.Summary, new SampleColumn("metadata", "metadata")];
        var table = SamplesTable.Read(BrowserLog, columns);
        Assert.Contains("metadata_eval", table.Columns);
        Assert.Contains("metadata_sample", table.Columns);
        Assert.True(table.IndexOf("metadata_eval") < table.IndexOf("metadata_sample"));
        Assert.Equal("{}", table[0, "metadata_eval"]);
    }

    [Fact]
    public void samples_table_full_metadata_and_exclude_fields()
    {
        var summary = SamplesTable.Read(PopularityLog);
        var table = SamplesTable.Read(PopularityLog, full: true);
        Assert.Equal(summary.Columns, table.Columns);
        Assert.IsType<double>(table[0, "metadata_label_confidence"]);
        Assert.Equal(summary[0, "metadata_label_confidence"], table[0, "metadata_label_confidence"]);
        Assert.StartsWith("{", (string)table[0, "metadata_nested"]!, StringComparison.Ordinal);
        Assert.Equal(summary[0, "metadata_nested"], table[0, "metadata_nested"]);
        var excluded = SamplesTable.Read(NewChoicesLog, [.. SampleColumns.Summary, .. SampleColumns.Messages], excludeFields: new HashSet<string> { "store" });
        Assert.True(excluded.RowCount > 0);
    }

    [Fact]
    public void samples_table_choices_column_reads_summary_or_full_sample()
    {
        Assert.All(SamplesTable.Read(NewChoicesLog).Column("choices"), cell => Assert.NotNull(cell));
        Assert.All(SamplesTable.Read(OldChoicesLog).Column("choices"), Assert.Null);

        IReadOnlyList<Column> auto = [new SampleColumn("id", "id", required: true, type: ColumnType.String), new SampleColumn("choices", "choices")];
        Assert.All(SamplesTable.Read(OldChoicesLog, auto).Column("choices"), cell => Assert.NotNull(cell));
        Assert.All(SamplesTable.Read(NewChoicesLog, auto).Column("choices"), cell => Assert.NotNull(cell));

        IReadOnlyList<Column> summaryOnly = [new SampleColumn("id", "id", required: true, type: ColumnType.String), new SampleColumn("choices", "choices", full: false)];
        Assert.All(SamplesTable.Read(OldChoicesLog, summaryOnly).Column("choices"), Assert.Null);
        Assert.All(SamplesTable.Read(NewChoicesLog, summaryOnly).Column("choices"), cell => Assert.NotNull(cell));
    }

    // ---------------------------------------------------------------- messages and events

    [Fact]
    public void messages_table_has_pythons_default_columns_and_one_row_per_message()
    {
        var table = MessagesTable.Read(LogsDir);
        Assert.Equal(34, table.RowCount);
        Assert.Equal(MessagesDefaultColumns, table.Columns);
        Assert.Equal(14, MessagesTable.Read(LogsDir, filter: message => message.Role == "assistant").RowCount);
        Assert.Equal(15, MessagesTable.Read(EvalLogFiles.ReadEvalLog(SecurityGuideLog)).RowCount);

        IReadOnlyList<Column> columns = [.. EvalColumns.Model, .. MessageColumns.Default];
        var withModel = MessagesTable.Read(LogsDir, columns);
        Assert.Equal(4 + EvalColumns.Model.Count + MessageColumns.Default.Count, withModel.ColumnCount);
        Assert.Contains("model", withModel.Columns);
    }

    [Fact]
    public void messages_table_values_match_the_venv_for_the_browser_log()
    {
        var table = MessagesTable.Read(BrowserLog);
        Assert.Equal("QQZXTTQY46SAxcBZie6XDi", table[0, "message_id"]);
        Assert.Equal("ATYFNjyWUz4mZ5Dgj6yd4f", table[0, "sample_id"]);
        Assert.Equal("user", table[0, "role"]);
        Assert.Equal("input", table[0, "source"]);
        Assert.StartsWith("Use the web browser tool", (string)table[0, "content"]!, StringComparison.Ordinal);
        Assert.Null(table[0, "tool_calls"]);
        Assert.Equal(1L, table[0, "order"]);
        Assert.Equal("Ygng6oBbbLpQSY59fm83pB", table[1, "message_id"]);
        Assert.Equal("assistant", table[1, "role"]);
        Assert.Equal("generate", table[1, "source"]);
        Assert.Equal("", table[1, "content"]);
        Assert.Equal("web_browser_go(url='https://www.aisi.gov.uk/')", table[1, "tool_calls"]);
        Assert.Equal(2L, table[1, "order"]);
    }

    [Fact]
    public void events_table_has_pythons_default_columns_and_one_row_per_event()
    {
        var table = EventsTable.Read(LogsDir);
        Assert.Equal(124, table.RowCount);
        Assert.Equal(EventsDefaultColumns, table.Columns);
        Assert.Equal(4, EventsTable.Read(LogsDir, filter: e => e.Event == "tool").RowCount);
        Assert.Equal(42, EventsTable.Read(EvalLogFiles.ReadEvalLog(SecurityGuideLog)).RowCount);

        IReadOnlyList<Column> columns = [.. EvalColumns.Model, .. EventColumns.Info, .. EventColumns.Timing];
        var withModel = EventsTable.Read(LogsDir, columns);
        Assert.Equal(4 + EvalColumns.Model.Count + EventColumns.Info.Count + EventColumns.Timing.Count, withModel.ColumnCount);
    }

    [Fact]
    public void events_table_model_and_tool_columns_match_the_venv()
    {
        IReadOnlyList<Column> modelColumns = [.. EventColumns.Info, .. EventColumns.Timing, .. EventColumns.ModelEvent];
        var model = EventsTable.Read(BrowserLog, modelColumns, filter: e => e.Event == "model");
        Assert.Equal("W6JGBuYFCWgXCFpYP6Mm6K", model[0, "event_id"]);
        Assert.Equal("model", model[0, "event"]);
        Assert.Equal("e758269d7b5e411fb689337dd84ea1d6", model[0, "span_id"]);
        Assert.Equal(new DateTimeOffset(2025, 5, 13, 0, 27, 38, TimeSpan.Zero).AddTicks(4621800), model[0, "timestamp"]);
        Assert.Equal(new DateTimeOffset(2025, 5, 13, 0, 27, 39, TimeSpan.Zero).AddTicks(6971590), model[0, "completed"]);
        Assert.Equal(0.008466166444122791, model[0, "working_start"]);
        Assert.Equal(1.1010224996134639, model[0, "working_time"]);
        Assert.Equal("openai/gpt-4o-mini", model[0, "model_event_model"]);
        Assert.Null(model[0, "model_event_role"]);
        Assert.StartsWith("user:\nUse the web browser tool", (string)model[0, "model_event_input"]!, StringComparison.Ordinal);
        Assert.StartsWith("[{\"name\": \"web_browser_go\", \"description\": ", (string)model[0, "model_event_tools"]!, StringComparison.Ordinal);
        Assert.Equal("auto", model[0, "model_event_tool_choice"]);
        Assert.Equal("{\"parallel_tool_calls\": false}", model[0, "model_event_config"]);
        Assert.Equal("{\"input_tokens\": 1124, \"output_tokens\": 23, \"total_tokens\": 1147, \"input_tokens_cache_read\": 0, \"reasoning_tokens\": 0}", model[0, "model_event_usage"]);
        Assert.Equal(1.1010224996134639, model[0, "model_event_time"]);
        Assert.Equal("", model[0, "model_event_completion"]);
        Assert.Null(model[0, "model_event_retries"]);
        Assert.Null(model[0, "model_event_error"]);
        Assert.StartsWith("{\"request\": {\"messages\": [{\"role\": \"user\", \"content\": \"Use t", (string)model[0, "model_event_call"]!, StringComparison.Ordinal);
        Assert.Equal(1L, model[0, "order"]);

        IReadOnlyList<Column> toolColumns = [.. EventColumns.Info, .. EventColumns.Timing, .. EventColumns.ToolEvent];
        var tool = EventsTable.Read(LogsDir, toolColumns, filter: e => e.Event == "tool");
        Assert.Equal(4, tool.RowCount);
        var browser = Enumerable.Range(0, tool.RowCount).First(r => (string)tool[r, "sample_id"]! == "ATYFNjyWUz4mZ5Dgj6yd4f");
        Assert.Equal("W6JGBuYFCWgXCFpYP6Mm6K", tool[browser, "event_id"]);
        Assert.Equal("web_browser_go", tool[browser, "tool_event_function"]);
        Assert.Equal("{\"url\": \"https://www.aisi.gov.uk/\"}", tool[browser, "tool_event_arguments"]);
        Assert.Null(tool[browser, "tool_event_view"]);
        Assert.StartsWith("[132] link \"We are now the AI Security Institute\"", (string)tool[browser, "tool_event_result"]!, StringComparison.Ordinal);
        Assert.Equal(4.317574, (double)tool[browser, "working_time"]!, 6);
        Assert.Null(tool[browser, "tool_event_error_type"]);
    }

    [Fact]
    public void events_table_model_columns_over_other_events_fail_like_python()
    {
        var ex = Assert.Throws<ColumnImportException>(() => EventsTable.Read(BrowserLog, EventColumns.ModelEvent));
        Assert.Equal("model_event_input", ex.Error.Column);
        Assert.Contains("has no attribute 'input'", ex.Message, StringComparison.Ordinal);

        var import = EventsTable.ReadWithErrors(BrowserLog, EventColumns.ModelEvent);
        Assert.NotEmpty(import.Errors);
        Assert.All(import.Errors, error => Assert.Contains(error.Column, new[] { "model_event_input", "model_event_tool_choice", "model_event_completion" }));
    }

    // ---------------------------------------------------------------- empty inputs, errors and log sources

    [Fact]
    public void empty_log_directories_and_lists_give_empty_tables()
    {
        using var temp = new TempDir();
        Assert.Equal(0, EvalsTable.Read(temp.Path).RowCount);
        Assert.Equal(0, SamplesTable.Read(temp.Path).RowCount);
        Assert.Equal(0, MessagesTable.Read(temp.Path).RowCount);
        Assert.Equal(0, EventsTable.Read(temp.Path).RowCount);

        var none = Array.Empty<string>();
        Assert.Equal(0, EvalsTable.Read(none).RowCount);
        Assert.Equal(0, SamplesTable.Read(none).RowCount);
        Assert.Equal(0, MessagesTable.Read(none).RowCount);
        Assert.Equal(0, EventsTable.Read(none).RowCount);
        Assert.Equal(0, EvalsTable.Read(Array.Empty<EvalLog>()).RowCount);
        Assert.Equal(0, EvalsTable.Read(temp.Path).ColumnCount);

        using var env = new EnvVarScope().Set("INSPECT_LOG_DIR", temp.Path);
        Assert.Equal(0, EvalsTable.Read().RowCount);
    }

    [Fact]
    public void log_sources_resolve_files_directories_infos_and_ignore_non_log_files()
    {
        using var temp = new TempDir();
        File.Copy(BrowserLog, Path.Combine(temp.Path, Path.GetFileName(BrowserLog)));
        File.WriteAllText(Path.Combine(temp.Path, "notes.json"), "{}");
        Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        File.Copy(SecurityGuideLog, Path.Combine(temp.Path, "nested", Path.GetFileName(SecurityGuideLog)));

        Assert.Equal(2, EvalsTable.Read(temp.Path).RowCount);
        Assert.Equal(1, EvalsTable.Read(Path.Combine(temp.Path, Path.GetFileName(BrowserLog))).RowCount);
        Assert.Equal(2, EvalsTable.Read(new[] { Path.Combine(temp.Path, Path.GetFileName(BrowserLog)), Path.Combine(temp.Path, "nested") }).RowCount);
        Assert.Equal(2, EvalsTable.Read(EvalLogFiles.ListEvalLogs(temp.Path).ToArray()).RowCount);
        Assert.Equal(1, EvalsTable.Read(new[] { BrowserLog, BrowserLog }).RowCount);
        Assert.Throws<FileNotFoundException>(() => EvalsTable.Read(Path.Combine(temp.Path, "missing.json")));
    }

    [Fact]
    public void strict_reads_raise_the_first_column_error_and_lenient_reads_collect_them()
    {
        IReadOnlyList<Column> columns = [new EvalColumn("missing", "eval.nope", required: true)];
        var ex = Assert.Throws<ColumnImportException>(() => EvalsTable.Read(LogsDir, columns));
        Assert.Equal("missing", ex.Error.Column);
        Assert.Equal("eval.nope", ex.Error.Path);
        Assert.StartsWith("Error reading column 'missing' from path 'eval.nope': field not found (log: ", ex.Message, StringComparison.Ordinal);

        var import = EvalsTable.ReadWithErrors(LogsDir, columns);
        Assert.Equal(4, import.Errors.Count);
        Assert.Equal(4, import.Table.RowCount);
        Assert.All(import.Table.Column("missing"), Assert.Null);

        IReadOnlyList<Column> badType = [new EvalColumn("created_int", "eval.created", type: ColumnType.Int)];
        var typed = EvalsTable.ReadWithErrors(BrowserLog, badType);
        Assert.Single(typed.Errors);
        Assert.Contains("Cannot coerce", typed.Errors[0].Error.Message, StringComparison.Ordinal);

        IReadOnlyList<Column> unsupported = [new EvalColumn("filtered", "results.scores[?(@.name == 'x')].name")];
        Assert.Throws<ColumnImportException>(() => EvalsTable.Read(BrowserLog, unsupported));

        Assert.Throws<ArgumentException>(() => SamplesTable.Read(BrowserLog, MessageColumns.Default));
    }

    [Fact]
    public async Task async_reads_return_the_same_tables_and_honour_cancellation()
    {
        var table = await EvalsTable.ReadAsync(LogsDir);
        Assert.Equal(4, table.RowCount);
        var samples = await SamplesTable.ReadWithErrorsAsync(BrowserLog);
        Assert.Equal(1, samples.Table.RowCount);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MessagesTable.ReadAsync(LogsDir, cancellationToken: cts.Token));
    }

    // ---------------------------------------------------------------- column mechanics

    [Fact]
    public void expand_fields_matches_python()
    {
        AssertExpanded("foo_*", """{"x": 1, "y": 2}""", ("foo_x", "1"), ("foo_y", "2"));
        AssertExpanded("foo_*_*", """{"first": {"x": 1, "y": 2}, "second": {"z": 3}}""", ("foo_first_x", "1"), ("foo_first_y", "2"), ("foo_second_z", "3"));
        AssertExpanded("*_suffix", """{"a": 1, "b": 2}""", ("a_suffix", "1"), ("b_suffix", "2"));
        AssertExpanded("simple_field", """{"a": 1, "b": 2}""", ("simple_field", """{"a":1,"b":2}"""));
        AssertExpanded("foo_*_bar_*", """{"x": {"y": 1, "z": 2}}""", ("foo_x_bar_y", "1"), ("foo_x_bar_z", "2"));
        AssertExpanded("foo_*", "{}");
        AssertExpanded("foo_*", "123");
        AssertExpanded("foo_*_*", """{"first": 123, "second": {"x": 1}}""", ("foo_second_x", "1"));
        AssertExpanded("level1_*_level2_*_level3_*", """{"a": {"b": {"c": 1, "d": 2}, "e": {"f": 3}}}""", ("level1_a_level2_b_level3_c", "1"), ("level1_a_level2_b_level3_d", "2"), ("level1_a_level2_e_level3_f", "3"));
        AssertExpanded("foo_*", """{"a": 1, "b": "string", "c": true, "d": null, "e": [1, 2, 3]}""", ("foo_a", "1"), ("foo_b", "\"string\""), ("foo_c", "true"), ("foo_d", null), ("foo_e", "[1,2,3]"));
        var complex = RecordImporter.ExpandFields("stats_*_*", JsonNode.Parse("""{"user": {"id": 123, "name": "test_user", "active": true}, "metrics": {"views": 1000, "clicks": 50, "details": {"source": "web", "campaign": "spring"}}, "empty": {}}""")).ToDictionary(pair => pair.Key, pair => pair.Value?.ToJsonString());
        Assert.Equal("123", complex["stats_user_id"]);
        Assert.Equal("\"test_user\"", complex["stats_user_name"]);
        Assert.Equal("true", complex["stats_user_active"]);
        Assert.Equal("1000", complex["stats_metrics_views"]);
        Assert.Equal("""{"source":"web","campaign":"spring"}""", complex["stats_metrics_details"]);
    }

    [Fact]
    public void values_coerce_like_pythons_resolve_value()
    {
        Assert.Null(RecordImporter.ResolveValue(null, ColumnType.Int));
        Assert.Equal("[1, \"a\", null]", RecordImporter.ResolveValue(JsonNode.Parse("[1, \"a\", null]"), null));
        Assert.Equal("{\"k\": \"\\u00e9\"}", RecordImporter.ResolveValue(JsonNode.Parse("{\"k\": \"\u00e9\"}"), ColumnType.String));
        Assert.Throws<FormatException>(() => RecordImporter.ResolveValue(JsonNode.Parse("[1]"), ColumnType.Int));
        Assert.Equal(5L, RecordImporter.ResolveValue(JsonValue.Create("5"), ColumnType.Int));
        Assert.Equal(5L, RecordImporter.ResolveValue(JsonValue.Create("5.5"), ColumnType.Int));
        Assert.Equal(2L, RecordImporter.ResolveValue(JsonValue.Create(2.7), ColumnType.Int));
        Assert.Equal(1L, RecordImporter.ResolveValue(JsonValue.Create(true), ColumnType.Int));
        Assert.Equal(1.5, RecordImporter.ResolveValue(JsonValue.Create("1.5"), ColumnType.Float));
        Assert.Equal(3.0, RecordImporter.ResolveValue(JsonValue.Create(3), ColumnType.Float));
        Assert.Equal(true, RecordImporter.ResolveValue(JsonValue.Create("true"), ColumnType.Bool));
        Assert.Equal(false, RecordImporter.ResolveValue(JsonValue.Create("off"), ColumnType.Bool));
        Assert.Equal(true, RecordImporter.ResolveValue(JsonValue.Create("1"), ColumnType.Bool));
        Assert.Throws<FormatException>(() => RecordImporter.ResolveValue(JsonValue.Create("abc"), ColumnType.Bool));
        Assert.Equal("1", RecordImporter.ResolveValue(JsonValue.Create(1), ColumnType.String));
        Assert.Equal("2.5", RecordImporter.ResolveValue(JsonValue.Create(2.5), ColumnType.String));
        Assert.Equal("True", RecordImporter.ResolveValue(JsonValue.Create(true), ColumnType.String));
        Assert.Equal(new DateTimeOffset(2025, 5, 13, 0, 27, 36, TimeSpan.Zero), RecordImporter.ResolveValue(JsonValue.Create("2025-05-12T20:27:36-04:00"), ColumnType.DateTime));
        Assert.Equal(new DateOnly(2025, 5, 13), RecordImporter.ResolveValue(JsonValue.Create("2025-05-12T20:27:36-04:00"), ColumnType.Date));
        Assert.Equal(new TimeOnly(0, 27, 36), RecordImporter.ResolveValue(JsonValue.Create("2025-05-12T20:27:36-04:00"), ColumnType.Time));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), RecordImporter.ResolveValue(JsonValue.Create(1704067200), ColumnType.DateTime));
        Assert.Equal(new DateOnly(2024, 1, 1), RecordImporter.ResolveValue(JsonValue.Create("1704067200"), ColumnType.Date));
        Assert.Throws<FormatException>(() => RecordImporter.ResolveValue(JsonValue.Create("not a date"), ColumnType.DateTime));
        Assert.Equal(double.NaN, RecordImporter.ResolveValue(JsonValue.Create(PythonJsonFormat.NaNSentinel), null));
        Assert.Equal("{\"v\": NaN}", RecordImporter.ResolveValue(JsonNode.Parse("{\"v\": \"\\u0001NaN\"}"), null));
    }

    [Fact]
    public void duplicate_columns_keep_the_later_definition_and_paths_resolve_first_match()
    {
        var deduped = RecordImporter.ResolveDuplicateColumns([new EvalColumn("a", "x"), new EvalColumn("b", "y"), new EvalColumn("a", "z")]);
        Assert.Equal(["b", "a"], deduped.Select(column => column.Name));
        Assert.Equal("z", deduped[1].Path);

        var root = JsonNode.Parse("""{"eval": {"task": "t", "scores": [{"name": "a"}, {"name": "b"}]}, "list": [10, 20]}""");
        Assert.Equal("\"t\"", JsonPath.FindFirst(root, "eval.task")?.ToJsonString());
        Assert.Equal("\"t\"", JsonPath.FindFirst(root, "$.eval.task")?.ToJsonString());
        Assert.Equal("\"b\"", JsonPath.FindFirst(root, "eval.scores[1].name")?.ToJsonString());
        Assert.Equal("\"a\"", JsonPath.FindFirst(root, "eval.scores[*].name")?.ToJsonString());
        Assert.Equal("20", JsonPath.FindFirst(root, "list[-1]")?.ToJsonString());
        Assert.Null(JsonPath.FindFirst(root, "eval.missing.deeper"));
        Assert.Throws<NotSupportedException>(() => JsonPath.FindFirst(root, "eval..task"));
    }

    [Fact]
    public void column_ordering_helpers_match_python()
    {
        string[] actual = ["z", "score_b", "score_a_eval", "name_sample", "name", "eval_id"];
        Assert.Equal(["name_sample"], RecordImporter.ResolveColumns("name", "_sample", actual, []));
        Assert.Equal(["name"], RecordImporter.ResolveColumns("name", "_eval", actual, []));
        Assert.Equal(["score_a_eval", "score_b"], RecordImporter.ResolveColumns("score_*", "_eval", actual, []));
        Assert.Equal(["eval_id", "name", "name_sample", "score_a_eval", "score_b", "z"], RecordImporter.AddUnreferencedColumns(actual, ["eval_id"]));
    }

    [Fact]
    public void auto_ids_and_text_renderings_match_python()
    {
        Assert.Equal("4gUwLpsPkTRNfKmRx7tusN", Extract.AutoId("abc", "1_1"));
        Assert.Equal("jj5sZGKkrcJRfxqgR9oFbv", Extract.AutoId("ZB2vu5GNujYaBdPSReCeop", "1_1"));
        Assert.Equal("StHzPCP8TfaGCkpQ7HmseU", Extract.AutoId("s", "message_0"));
        Assert.Equal("jj5sZGKkrcJRfxqgR9oFbv", SampleColumns.AutoSampleId("ZB2vu5GNujYaBdPSReCeop", 1, 1));
        Assert.Equal("StHzPCP8TfaGCkpQ7HmseU", SampleColumns.AutoDetailId("s", "message", 0));

        var arguments = (JsonObject)JsonNode.Parse("""{"b": [1, 2.5, "x"], "a": {"k": true, "n": null}, "s": "it's"}""")!;
        var assistant = new ChatMessageAssistant("Hi there", [new ToolCall("1", "f", arguments)]);
        Assert.Equal("assistant:\nHi there\n\nTool Call: f\nArguments:\nb: [1, 2.5, 'x']\na: {'k': True, 'n': None}\ns: it's", Extract.MessageAsStr(assistant));
        Assert.Equal("f(b=[1, 2.5, 'x'], a={'k': True, 'n': None}, s='it's')", ((JsonValue)MessageColumns.MessageToolCalls(assistant)!).GetValue<string>());
        var tool = new ChatMessageTool("out", function: "bash", error: new ToolCallError("timeout", "took too long"));
        Assert.Equal("tool:\nout\n\nError in tool call 'bash':\ntook too long\n", Extract.MessageAsStr(tool));
        Assert.Equal("user:\nq\n\n\nassistant:\nHi there\n\nTool Call: f\nArguments:\nb: [1, 2.5, 'x']\na: {'k': True, 'n': None}\ns: it's\n\ntool:\nout\n\nError in tool call 'bash':\ntook too long\n", Extract.MessagesAsStr([new ChatMessageUser("  q  "), assistant, tool]));

        var sorted = (JsonObject)JsonNode.Parse("""{"d": {"z": 1, "a": [1, 2]}, "e": 1.0, "f": null, "g": false}""")!;
        Assert.Equal("f(d={'a': [1, 2], 'z': 1}, e=1.0, f=None, g=False)", PythonFormat.FormatFunctionCall("f", sorted, width: 1000));
        var wide = (JsonObject)JsonNode.Parse($$$"""{"x": "{{{new string('a', 990)}}}", "y": 1}""")!;
        Assert.Equal($"f(\n    x='{new string('a', 990)}',\n    y=1\n)", PythonFormat.FormatFunctionCall("f", wide, width: 1000));

        var view = new ToolCallContent("text", "run {{cmd}} with {{n}} and {{missing}}") { Title = "Title {{cmd}}" };
        var substituted = EventColumns.SubstituteToolCallContent(view, (JsonObject)JsonNode.Parse("""{"cmd": "ls", "n": 2}""")!);
        Assert.Equal("Title ls", substituted.Title);
        Assert.Equal("run ls with 2 and {{missing}}", substituted.Content);
        Assert.Equal("{\"match\":\"C\"}", ((JsonObject)Extract.ScoreDetails(JsonNode.Parse("""{"match": {"value": "C", "answer": "", "reason": null}}"""))!).ToJsonString());
        var details = (JsonObject)Extract.ScoreDetails(JsonNode.Parse("""{"match": {"value": "I", "answer": "foo", "reason": "invalid_response_format"}, "other": {"value": "C"}}"""))!;
        Assert.Equal("\"I\"", details["match"]!.ToJsonString());
        Assert.Equal("\"invalid_response_format\"", details["match_reason"]!.ToJsonString());
        Assert.Equal("\"foo\"", details["match_answer"]!.ToJsonString());
        Assert.False(details.ContainsKey("other_reason"));
    }

    [Fact]
    public void messages_from_events_deduplicate_and_collapse_repeated_assistant_messages()
    {
        var user = new ChatMessageUser("hi") { Id = "u1" };
        var assistant = new ChatMessageAssistant("hello") { Id = "a1" };
        var again = new ChatMessageAssistant("hello") { Id = "a2" };
        var different = new ChatMessageAssistant("hello", [new ToolCall("t", "bash", new JsonObject())]) { Id = "a3" };
        var unnamed = new ChatMessageUser("no id") { Id = null };
        var events = new List<TranscriptEvent>
        {
            ModelEventFor([user], assistant),
            ModelEventFor([user, assistant], again),
            ModelEventFor([user, assistant, again, unnamed], different),
            new InfoEvent(null, null),
        };
        var messages = SamplesTable.SampleMessagesFromEvents(events, null);
        Assert.Equal(["u1", "a1", null, "a3"], messages.Select(message => message.Id));
        Assert.Equal(["a1", "a3"], SamplesTable.SampleMessagesFromEvents(events, message => message.Role == "assistant").Select(message => message.Id));
    }

    // ---------------------------------------------------------------- Table

    [Fact]
    public void table_csv_writer_escapes_like_rfc_4180()
    {
        var table = new Table(["a", "b,c", "d"],
        [
            new object?[] { "x,y", 1L, null },
            new object?[] { "say \"hi\"", 2.5, true },
            new object?[] { "line\nbreak", double.NaN, new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero) },
            new object?[] { "", 100000000000000000L, new DateOnly(2024, 2, 29) },
        ]);
        var expected = "a,\"b,c\",d\n\"x,y\",1,\n\"say \"\"hi\"\"\",2.5,True\n\"line\nbreak\",,2025-01-02T03:04:05+00:00\n,100000000000000000,2024-02-29\n";
        Assert.Equal(expected, table.ToCsv());

        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "out.csv");
        table.WriteCsv(path);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal("", Table.Empty.ToCsv().TrimEnd('\n'));
    }

    [Fact]
    public void table_json_lines_writer_emits_one_object_per_row()
    {
        var table = new Table(["a", "n", "f", "t"],
        [
            new object?[] { "x\"y", 1L, 2.5, new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero) },
            new object?[] { null, -3L, double.NaN, false },
        ]);
        Assert.Equal("{\"a\":\"x\\\"y\",\"n\":1,\"f\":2.5,\"t\":\"2025-01-02T03:04:05+00:00\"}\n{\"a\":null,\"n\":-3,\"f\":null,\"t\":false}\n", table.ToJsonLines());
    }

    [Fact]
    public void table_operations_select_replace_merge_and_validate()
    {
        var table = Table.FromRecords(
        [
            new Dictionary<string, object?> { ["k"] = "a", ["v"] = 1 },
            new Dictionary<string, object?> { ["k"] = "b", ["w"] = 2.5f },
        ]);
        Assert.Equal(["k", "v", "w"], table.Columns);
        Assert.Equal(1L, table[0, "v"]);
        Assert.Null(table[0, "w"]);
        Assert.Equal(2.5, table[1, "w"]);
        Assert.Equal(["w", "k"], table.Select(["w", "k"]).Columns);
        Assert.Throws<ArgumentException>(() => table.Select(["nope"]));
        Assert.Throws<KeyNotFoundException>(() => table[0, "nope"]);
        Assert.Throws<ArgumentException>(() => new Table(["a"], [new object?[] { new object() }]));
        Assert.Throws<ArgumentException>(() => new Table(["a", "a"], []));
        Assert.Throws<ArgumentException>(() => table.WithColumn("x", [1L]));

        var replaced = table.WithColumn("v", [10L, 20L]).WithColumn("x", [true, false]);
        Assert.Equal(["k", "v", "w", "x"], replaced.Columns);
        Assert.Equal(20L, replaced[1, "v"]);

        var right = new Table(["k", "v", "extra"], [new object?[] { "a", "right", "e" }, new object?[] { "a", "right2", "e2" }]);
        var merged = table.LeftMerge(right, "k", "_left", "_right");
        Assert.Equal(["k", "v_left", "w", "v_right", "extra"], merged.Columns);
        Assert.Equal(3, merged.RowCount);
        Assert.Equal("right2", merged[1, "v_right"]);
        Assert.Null(merged[2, "extra"]);
        Assert.Equal(1, new Table(["id"], [new object?[] { "a" }, new object?[] { "a" }, new object?[] { null }]).DistinctBy("id").RowCount + 0 - 1);
        Assert.Equal(["k", "v", "w"], table.Record(0).Keys);
    }

    // ---------------------------------------------------------------- prepare

    [Fact]
    public void score_to_float_converts_scores_and_missing_values()
    {
        var table = Prepare.Apply(SamplesTable.Read(BrowserLog), Prepare.ScoreToFloat(["score_includes"]));
        Assert.Equal([1.0], table.Column("score_includes").Cast<object>());

        var all = SamplesTable.Read(LogsDir);
        Assert.Contains(null, all.Column("score_match"));
        var converted = Prepare.Apply(all, Prepare.ScoreToFloat("score_match"));
        for (var r = 0; r < all.RowCount; r++)
        {
            var value = (double)converted[r, "score_match"]!;
            if (all[r, "score_match"] is null)
            {
                Assert.True(double.IsNaN(value));
            }
            else
            {
                Assert.False(double.IsNaN(value));
            }
        }

        Assert.Same(Table.Empty, Prepare.Apply(Table.Empty, Prepare.ScoreToFloat("x")));
        Assert.Throws<ArgumentException>(() => Prepare.Apply(all, Prepare.ScoreToFloat("nope")));
        var custom = Prepare.Apply(new Table(["s"], [new object?[] { "yes" }, new object?[] { 2L }]), Prepare.ScoreToFloat("s", value => value is Scorers.ScoreValue.Str { Value: "yes" } ? 1.0 : 0.25));
        Assert.Equal([1.0, 0.25], custom.Column("s").Cast<object>());
    }

    [Fact]
    public void task_info_maps_task_names_to_display_names()
    {
        Assert.Same(Table.Empty, Prepare.Apply(Table.Empty, Prepare.TaskInfo(new Dictionary<string, string>())));
        var table = Prepare.Apply(EvalsTable.Read(BrowserLog), Prepare.TaskInfo(new Dictionary<string, string>()));
        Assert.Equal("browser", table[0, "task_display_name"]);
        table = Prepare.Apply(table, Prepare.TaskInfo(new Dictionary<string, string> { ["browser"] = "Browser Task" }));
        Assert.Equal("Browser Task", table[0, "task_display_name"]);
        var bare = Prepare.Apply(new Table(["task_name"], [new object?[] { "t1" }]), Prepare.TaskInfo(new Dictionary<string, string>()));
        Assert.Equal("t1", bare[0, "task_display_name"]);
        Assert.Throws<ArgumentException>(() => Prepare.Apply(new Table(["model"], [new object?[] { "m" }]), Prepare.TaskInfo(new Dictionary<string, string>())));
    }

    [Fact]
    public void model_info_adds_model_metadata_columns()
    {
        Assert.Same(Table.Empty, Prepare.Apply(Table.Empty, Prepare.ModelInfo()));
        var ex = Assert.Throws<ArgumentException>(() => Prepare.Apply(new Table(["task_name"], [new object?[] { "t1" }]), Prepare.ModelInfo()));
        Assert.Contains("Required column 'model' not found", ex.Message, StringComparison.Ordinal);

        var table = Prepare.Apply(new Table(["model"], [new object?[] { "openai/gpt-4o" }, new object?[] { "unknown_model" }]), Prepare.ModelInfo());
        Assert.Equal(["model", "model_organization_name", "model_display_name", "model_snapshot", "model_release_date", "model_knowledge_cutoff_date"], table.Columns);
        Assert.Equal(["GPT-4o", "unknown_model"], table.Column("model_display_name").Cast<object>());
        Assert.Equal("OpenAI", table[0, "model_organization_name"]);
        Assert.IsType<DateOnly>(table[0, "model_release_date"]);
        Assert.Null(table[1, "model_organization_name"]);

        var custom = new Dictionary<string, Model.Cost.ModelInfo> { ["unknown_model"] = new() { Organization = "Acme", Model = "Unknown One", ReleaseDate = new DateOnly(2025, 1, 1) } };
        table = Prepare.Apply(new Table(["model"], [new object?[] { "unknown_model" }]), Prepare.ModelInfo(custom));
        Assert.Equal("Unknown One", table[0, "model_display_name"]);
        Assert.Equal("Acme", table[0, "model_organization_name"]);
        Assert.Equal(new DateOnly(2025, 1, 1), table[0, "model_release_date"]);
    }

    [Fact]
    public void frontier_marks_the_best_model_at_each_release_date()
    {
        var table = new Table(["task_name", "model_release_date", "score_headline_value"],
        [
            new object?[] { "t", "2024-01-01", double.NaN },
            new object?[] { "t", "2024-01-01", double.NaN },
            new object?[] { "t", "2024-02-01", 0.5 },
            new object?[] { "t", "2024-03-01", 0.7 },
        ]);
        var frontier = Prepare.Apply(table, Prepare.Frontier());
        Assert.Equal([false, false, true, true], frontier.Column("frontier").Cast<object>());

        var mixed = new Table(["task_name", "model_release_date", "score_headline_value"],
        [
            new object?[] { "t", new DateOnly(2024, 3, 1), 0.6 },
            new object?[] { "t", new DateOnly(2024, 1, 1), 0.5 },
            new object?[] { "t", new DateOnly(2024, 1, 1), 0.8 },
            new object?[] { "t", new DateOnly(2024, 2, 1), 0.9 },
            new object?[] { "u", new DateOnly(2024, 2, 1), 0.1 },
            new object?[] { "u", null, 0.9 },
            new object?[] { null, new DateOnly(2024, 2, 1), 0.9 },
        ]);
        Assert.Equal([false, false, true, true, true, false, false], Prepare.Apply(mixed, Prepare.Frontier()).Column("frontier").Cast<object>());
        Assert.Same(Table.Empty, Prepare.Apply(Table.Empty, Prepare.Frontier()));
        Assert.Throws<ArgumentException>(() => Prepare.Apply(new Table(["task_name"], [new object?[] { "t" }]), Prepare.Frontier()));

        var evals = Prepare.Apply(EvalsTable.Read(LogsDir), Prepare.ModelInfo(), Prepare.Frontier());
        Assert.Contains("frontier", evals.Columns);
    }

    // ---------------------------------------------------------------- python cross-checks (INSPECT_PY)

    [PythonInteropFact]
    public void tables_match_the_venvs_dataframes()
    {
        var script = $$"""
            import json, math
            import pandas as pd
            from inspect_ai.analysis import evals_df, samples_df, messages_df, events_df, EvalInfo, SampleSummary, SampleColumn, EventInfo, EventTiming, ToolEventColumns
            LOGS = {{JsonSerializer.Serialize(LogsDir)}}
            def cell(v):
                if v is None or v is pd.NA or (isinstance(v, float) and math.isnan(v)): return None
                if hasattr(v, "isoformat"): return v.isoformat()
                if hasattr(v, "item"): v = v.item()
                return round(v, 6) if isinstance(v, float) else v
            e = evals_df(LOGS, quiet=True)
            row = e[e["task_name"] == "browser"].iloc[0]
            evals_row = {c: cell(row[c]) for c in e.columns if c != "log"}
            evals_row["log"] = row["log"].rsplit("/", 1)[-1]
            s = samples_df(LOGS, quiet=True)
            merged = samples_df(LOGS, columns=EvalInfo + SampleSummary + [SampleColumn("metadata", path="metadata")], quiet=True)
            tools = events_df(LOGS, columns=EventInfo + EventTiming + ToolEventColumns, filter=lambda ev: ev.event == "tool", quiet=True)
            print(json.dumps({
                "evals_columns": list(e.columns), "evals_row": evals_row,
                "samples_columns": list(s.columns), "sample_ids": sorted(s["sample_id"].to_list()),
                "merged_columns": list(merged.columns),
                "messages_count": len(messages_df(LOGS, quiet=True)), "events_count": len(events_df(LOGS, quiet=True)),
                "tool_events": sorted(json.dumps({c: cell(tools.iloc[i][c]) for c in tools.columns if c != "log"}, sort_keys=True) for i in range(len(tools))),
            }))
            """;
        var expected = JsonNode.Parse(RunPython(script))!.AsObject();

        var evals = EvalsTable.Read(LogsDir);
        Assert.Equal(expected["evals_columns"]!.AsArray().Select(n => n!.GetValue<string>()), evals.Columns);
        var browser = Enumerable.Range(0, evals.RowCount).First(r => (string)evals[r, "task_name"]! == "browser");
        foreach (var (column, value) in expected["evals_row"]!.AsObject())
        {
            var actual = column == "log" ? Path.GetFileName((string)evals[browser, column]!) : evals[browser, column];
            Assert.True(JsonNode.DeepEquals(value, CellJson(actual)), $"evals column '{column}': python {value?.ToJsonString() ?? "null"} vs port {CellJson(actual)?.ToJsonString() ?? "null"}");
        }

        var samples = SamplesTable.Read(LogsDir);
        Assert.Equal(expected["samples_columns"]!.AsArray().Select(n => n!.GetValue<string>()), samples.Columns);
        Assert.Equal(expected["sample_ids"]!.AsArray().Select(n => n!.GetValue<string>()), samples.Column("sample_id").Select(c => (string)c!).Order(StringComparer.Ordinal));
        IReadOnlyList<Column> mergedColumns = [.. EvalColumns.Info, .. SampleColumns.Summary, new SampleColumn("metadata", "metadata")];
        Assert.Equal(expected["merged_columns"]!.AsArray().Select(n => n!.GetValue<string>()), SamplesTable.Read(LogsDir, mergedColumns).Columns);
        Assert.Equal(expected["messages_count"]!.GetValue<int>(), MessagesTable.Read(LogsDir).RowCount);
        Assert.Equal(expected["events_count"]!.GetValue<int>(), EventsTable.Read(LogsDir).RowCount);

        IReadOnlyList<Column> toolColumns = [.. EventColumns.Info, .. EventColumns.Timing, .. EventColumns.ToolEvent];
        var tools = EventsTable.Read(LogsDir, toolColumns, filter: e => e.Event == "tool");
        var portRows = Enumerable.Range(0, tools.RowCount)
            .Select(r => Provider.Util.PythonJson.Dumps(new JsonObject(tools.Columns.Where(c => c != "log").Order(StringComparer.Ordinal).Select(c => new KeyValuePair<string, JsonNode?>(c, CellJson(tools[r, c]))))))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected["tool_events"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal), portRows);
    }

    [PythonInteropFact]
    public void auto_ids_match_the_venvs_shortuuid_encoding()
    {
        var script = """
            import json
            from inspect_ai.analysis._dataframe.extract import auto_id
            cases = [("abc", "1_1"), ("", ""), ("ZB2vu5GNujYaBdPSReCeop", "x_2"), ("\u00e9\u00e8", "m_0"), ("s", "event_12")]
            print(json.dumps([auto_id(b, i) for b, i in cases]))
            """;
        var expected = JsonNode.Parse(RunPython(script))!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        (string Base, string Index)[] cases = [("abc", "1_1"), ("", ""), ("ZB2vu5GNujYaBdPSReCeop", "x_2"), ("\u00e9\u00e8", "m_0"), ("s", "event_12")];
        Assert.Equal(expected, cases.Select(c => Extract.AutoId(c.Base, c.Index)));
    }

    // ---------------------------------------------------------------- helpers

    private static JsonNode? CellJson(object? cell) => cell switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        double d when double.IsNaN(d) => null,
        double d => JsonValue.Create(Math.Round(d, 6)),
        string s => JsonValue.Create(s),
        DateTimeOffset dto => JsonValue.Create(PythonJsonFormat.FormatIso(dto)),
        _ => JsonValue.Create(Table.FormatCell(cell)),
    };

    private static void AssertExpanded(string name, string json, params (string Name, string? Json)[] expected)
    {
        var expanded = RecordImporter.ExpandFields(name, JsonNode.Parse(json)).Select(pair => (pair.Key, pair.Value?.ToJsonString())).ToList();
        Assert.Equal(expected.Select(e => (e.Name, e.Json)), expanded);
    }

    private static Dictionary<string, EvalMetric> Metrics(params (string Name, double Value)[] metrics) =>
        metrics.ToDictionary(metric => metric.Name, metric => new EvalMetric(metric.Name, metric.Value), StringComparer.Ordinal);

    private static EvalLog ScoredLog(IReadOnlyList<string>? reducers, params EvalScore[] scores) => new()
    {
        Status = EvalStatus.Success,
        Eval = new EvalSpec
        {
            Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Task = "t",
            Dataset = new EvalDataset(),
            Model = "test/model",
            Config = new EvalConfig { Epochs = reducers is null ? 2 : 4, EpochsReducer = reducers },
        },
        Results = new EvalResults { Scores = scores },
    };

    private static ModelEvent ModelEventFor(IReadOnlyList<ChatMessage> input, ChatMessageAssistant output) => new()
    {
        Model = "m",
        Input = input,
        ToolChoice = ToolChoice.Auto,
        Config = new GenerateConfig(),
        Output = new ModelOutput { Model = "m", Choices = [new ChatCompletionChoice(output, StopReason.Stop)] },
    };

    private static string FixtureRoot(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine([directory, "fixtures", .. parts]);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"No fixtures/{string.Join('/', parts)} above {AppContext.BaseDirectory}");
    }

    private static string RunPython(string script)
    {
        var interpreter = Environment.GetEnvironmentVariable("INSPECT_PY");
        if (string.IsNullOrEmpty(interpreter))
        {
            throw new InvalidOperationException("INSPECT_PY is not set.");
        }

        var start = new ProcessStartInfo(interpreter, "-") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inspect-analysis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
