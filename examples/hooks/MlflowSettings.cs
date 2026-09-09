namespace InspectAzureAI.Examples.Hooks;

/// <summary>
/// The environment the Python hooks read (<c>MLFLOW_TRACKING_URI</c>, <c>MLFLOW_EXPERIMENT_NAME</c>,
/// <c>MLFLOW_INSPECT_TRACING</c>, <c>MLFLOW_INSPECT_LOG_ARTIFACTS</c>) as one value, so a hook can be built either from
/// the process environment (the CLI's parameterless constructor, exactly Python's <c>os.getenv</c> checks) or from
/// explicit settings (the examples runner and the tests, which point it at a fake server).
/// </summary>
public sealed record MlflowSettings(
    string TrackingUri,
    string ExperimentName = MlflowSettings.DefaultExperimentName,
    bool Tracing = false,
    bool LogArtifacts = true)
{
    public const string TrackingUriVariable = "MLFLOW_TRACKING_URI";

    public const string ExperimentNameVariable = "MLFLOW_EXPERIMENT_NAME";

    public const string TracingVariable = "MLFLOW_INSPECT_TRACING";

    public const string LogArtifactsVariable = "MLFLOW_INSPECT_LOG_ARTIFACTS";

    /// <summary>Python's default experiment name (<c>os.getenv("MLFLOW_EXPERIMENT_NAME", "inspect_ai")</c>).</summary>
    public const string DefaultExperimentName = "inspect_ai";

    /// <summary>
    /// Port of the environment checks of both hooks: null when <c>MLFLOW_TRACKING_URI</c> is unset (the hooks are
    /// disabled), else the tracking URI, the experiment name (default <see cref="DefaultExperimentName"/>),
    /// whether <c>MLFLOW_INSPECT_TRACING</c> is <c>true</c> (case-insensitive) and whether
    /// <c>MLFLOW_INSPECT_LOG_ARTIFACTS</c> is anything but <c>false</c>.
    /// </summary>
    public static MlflowSettings? FromEnvironment()
    {
        var uri = Environment.GetEnvironmentVariable(TrackingUriVariable);
        if (uri is null)
        {
            return null;
        }

        return new MlflowSettings(
            uri,
            Environment.GetEnvironmentVariable(ExperimentNameVariable) ?? DefaultExperimentName,
            string.Equals(Environment.GetEnvironmentVariable(TracingVariable) ?? "", "true", StringComparison.OrdinalIgnoreCase),
            !string.Equals(Environment.GetEnvironmentVariable(LogArtifactsVariable) ?? "true", "false", StringComparison.OrdinalIgnoreCase));
    }
}
