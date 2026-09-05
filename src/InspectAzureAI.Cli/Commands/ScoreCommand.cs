using System.CommandLine;
using InspectAzureAI.Cli.Args;
using InspectAzureAI.Cli.Models;
using InspectAzureAI.Cli.Registry;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Commands;

/// <summary>
/// Port of <c>_cli/score.py</c>: re-scores a log with <c>--scorer</c> (built-in name plus <c>-S</c> args) or the scorers
/// recorded in its header, optionally with <c>--model</c> / <c>--model-role</c> and <c>--metric</c>, appends or
/// overwrites (<c>--action</c>), and writes the result to <c>--output-file</c>, over the input (<c>--overwrite</c>) or —
/// where Python would prompt — to a new <c>-scored</c> file, the prompt's default.
/// </summary>
internal sealed class ScoreCommand
{
    public CommonOptions Common { get; } = new();

    public Argument<string> LogFile { get; } = new Argument<string>("log-file") { Description = "Log file to score." }.RejectOptionLike();

    public Option<string?> Model { get; } = Opt.String("--model", "Model used for re-scoring (overrides the primary model recorded in the log).", "INSPECT_SCORE_MODEL");

    public Option<string?> ModelBaseUrl { get; } = Opt.String("--model-base-url", "Base URL for model API");

    public Option<string[]> M { get; } = Opt.Multi("-M", "One or more native model arguments (e.g. -M arg=value)", "INSPECT_SCORE_MODEL_ARGS");

    public Option<string[]> ModelRole { get; } = Opt.Multi("--model-role", "Named model role with model name or YAML/JSON config, e.g. --model-role critic=openai/gpt-4o. Merged over the model roles recorded in the log.", "INSPECT_SCORE_MODEL_ROLE");

    public Option<string?> Scorer { get; } = Opt.String("--scorer", "Scorer to use for scoring", "INSPECT_SCORE_SCORER");

    public Option<string[]> S { get; } = Opt.Multi("-S", "One or more scorer arguments (e.g. -S arg=value)", "INSPECT_SCORE_SCORER_ARGS");

    public Option<string[]> Metric { get; } = Opt.Multi("--metric", "Metric to use for scoring (overrides metrics in the log).", "INSPECT_SCORE_METRIC");

    public Option<string?> Action { get; } = Opt.Choice("--action", "Whether to append or overwrite the existing scores.", ["append", "overwrite"], "INSPECT_SCORE_SCORER_ACTION");

    public Option<bool> Overwrite { get; } = Opt.Flag("--overwrite", "Overwrite log file with the scored version", "INSPECT_SCORE_OVERWRITE");

    public Option<string?> OutputFile { get; } = Opt.String("--output-file", "Output file to write the scored log to.", "INSPECT_SCORE_OUTPUT_FILE");

    public Option<string?> Stream { get; } = Opt.FlagOrValue("--stream", "Stream the samples through the scoring process (not supported by this port).", "INSPECT_SCORE_STREAM");

    public Command Build(CliIo io)
    {
        var command = new Command("score", "Score a previous evaluation run.");
        command.Arguments.Add(LogFile);
        foreach (var option in new Option[] { Model, ModelBaseUrl, M, ModelRole, Scorer, S, Metric, Action, Overwrite, OutputFile, Stream })
        {
            command.Options.Add(option);
        }

        Common.AddTo(command);
        command.SetAction((result, cancellationToken) => RunAsync(result, io, cancellationToken));
        return command;
    }

    internal async Task<int> RunAsync(ParseResult result, CliIo io, CancellationToken cancellationToken)
    {
        Common.Process(result);
        var stream = Opt.FlagOrValueOf(result, Stream);
        if (stream.Given && CliArgs.IntOrBoolValue(stream.Value, 1, 0, isOneTrue: false) != 0)
        {
            throw new PrerequisiteError("--stream is not supported by this port: the log is read into memory and scored whole.");
        }

        var logFile = result.GetValue(LogFile)!;
        if (!File.Exists(logFile))
        {
            throw new PrerequisiteError($"Log file '{logFile}' does not exist.");
        }

        var scorerArgs = CliArgs.ParseCliConfig(result.GetValue(S), null);
        var modelArgs = CliArgs.ParseCliArgs(result.GetValue(M));
        var modelRoles = ModelRoleArgs.Parse(result.GetValue(ModelRole), (name, config, args) => ModelProviders.Resolve(name, config, null, args));
        var overrideModel = result.GetValue(Model) is { } modelName ? ModelProviders.Resolve(modelName, null, result.GetValue(ModelBaseUrl), modelArgs) : null;

        var log = await EvalLogFiles.ReadEvalLogAsync(logFile, cancellationToken: cancellationToken).ConfigureAwait(false);
        var samples = log.Samples?.Count ?? log.Results?.TotalSamples;
        if (samples is null or 0)
        {
            throw new PrerequisiteError($"Cannot determine the number of samples to score for {logFile}");
        }

        var coverage = log.Results;
        var scorers = result.GetValue(Scorer) is { } scorerName
            ? new List<ScorerDef> { Catalog.CreateScorer(scorerName, scorerArgs) }
            : Catalog.ScorersFromLog(log);
        if (scorers.Count == 0)
        {
            throw new PrerequisiteError("Unable to resolve any scorers for this log. Please specify a scorer using the '--scorer' param.");
        }

        var metrics = result.GetValue(Metric) is { Length: > 0 } metricNames ? metricNames.Select(Catalog.CreateMetric).ToList() : null;
        var action = result.GetValue(Action) is { } actionText
            ? (actionText == "overwrite" ? ScoreAction.Overwrite : ScoreAction.Append)
            : ScoreLogs.ResolveAction(log, null);
        var outputFile = ResolveOutputFile(logFile, result.GetValue(OutputFile), result.GetValue(Overwrite));

        var scored = await ScoreLogs.ScoreAsync(log, scorers, action, null, new ScoreLogOptions { Model = overrideModel, ModelRoles = modelRoles, Metrics = metrics, OutputPath = outputFile }, cancellationToken).ConfigureAwait(false);
        scored = scored with { Location = outputFile };
        await EvalLogFiles.WriteEvalLogAsync(scored, outputFile, cancellationToken: cancellationToken).ConfigureAwait(false);
        ResultsPrinter.Print(io.Out, scored with { Results = scored.Results is { } results && coverage is { } previous ? results with { TotalSamples = previous.TotalSamples, CompletedSamples = previous.CompletedSamples } : scored.Results }, outputFile);
        return 0;
    }

    /// <summary>
    /// Port of <c>_resolve_output_file</c> without its prompts: an explicit output file or <c>--overwrite</c> is honoured;
    /// otherwise an existing log is never touched and the prompt's default, <c>{stem}-scored.{ext}</c> (numbered when
    /// taken), is used.
    /// </summary>
    internal static string ResolveOutputFile(string logFile, string? outputFile, bool overwrite)
    {
        var output = outputFile ?? logFile;
        if (!File.Exists(output) || overwrite)
        {
            return output;
        }

        var directory = Path.GetDirectoryName(output) ?? "";
        var fileName = Path.GetFileName(output);
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? fileName : fileName[..dot];
        var extension = dot < 0 ? "" : fileName[(dot + 1)..];
        var candidate = Path.Combine(directory, $"{stem}-scored.{extension}");
        var count = 0;
        while (File.Exists(candidate))
        {
            count++;
            candidate = Path.Combine(directory, $"{stem}-scored-{count}.{extension}");
        }

        return candidate;
    }
}
