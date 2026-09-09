using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>How an attempt's exit is handled (the counterpart of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeExitKind"/>).</summary>
public enum CopilotCliExitKind
{
    /// <summary>Effective exit code 0.</summary>
    Success,

    /// <summary>
    /// Exit 1 after a refusal: the last bridged generation stopped with <c>content_filter</c>, which is already in
    /// the state and scorable. Carried over from the Claude Code rule and NOT verified against the Copilot CLI
    /// (neither the probe nor the live samples produced a <c>content_filter</c> stop); a future probe should confirm
    /// how 1.0.83 exits after one.
    /// </summary>
    Refusal,

    /// <summary>Exit 1 with empty stderr and retries left: the CLI gave up (its own model retries exhausted, say) and the session can be resumed.</summary>
    RetryUncaughtError,

    /// <summary>Anything else: a hard failure.</summary>
    Failure,
}

/// <summary>
/// The exit rules of the Copilot CLI agent, the counterpart of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeExit"/>
/// (inspect_swe <c>_is_claude_code_refusal_exit</c> and the retry rule). The success and give-up paths are wired to
/// what the 1.0.83 probe observed (the refusal rule is inherited from Claude Code, unverified here, see
/// <see cref="CopilotCliExitKind.Refusal"/>): the real binary exits 0 on success and 1 when it gives up, stderr stays empty, and the final <c>result</c> line
/// carries an <c>exitCode</c> that is reliable even when the process exit is not (a launcher shim), so the
/// effective code is the process exit when non-zero, else the result's. One exception was observed live (Docker,
/// eight samples at once): the self-contained Linux binary writes <c>Package extraction took NNNNms</c> to stderr
/// when its first start is slow; that notice is not an error and is ignored by every rule.
/// </summary>
public static partial class CopilotCliExit
{
    /// <summary>The stderr that counts: every line except the binary's own startup notice (<c>Package extraction took 5026ms</c>).</summary>
    public static string SignificantStderr(string stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        return StartupNoticeRegex().Replace(stderr, "").Trim();
    }

    /// <summary>The exit code the classification uses: a non-zero process exit wins; otherwise the <c>result</c> event's, else 0.</summary>
    public static int EffectiveExitCode(int processExitCode, int? resultExitCode) =>
        processExitCode != 0 ? processExitCode : resultExitCode ?? 0;

    /// <summary>Exit 1, no stderr, and the last bridged generation stopped with <c>content_filter</c> (Inspect's mapping of a refusal); the Claude Code rule, not yet observed with the Copilot CLI.</summary>
    public static bool IsRefusalExit(int exitCode, string stderr, StopReason? lastStopReason)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        if (exitCode != 1 || SignificantStderr(stderr).Length > 0)
        {
            return false;
        }

        return lastStopReason == StopReason.ContentFilter;
    }

    public static CopilotCliExitKind Classify(int exitCode, string stderr, StopReason? lastStopReason, int? retryUncaughtErrors, int uncaughtErrorCount)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        if (exitCode == 0)
        {
            return CopilotCliExitKind.Success;
        }

        if (IsRefusalExit(exitCode, stderr, lastStopReason))
        {
            return CopilotCliExitKind.Refusal;
        }

        if (exitCode == 1 && SignificantStderr(stderr).Length == 0 && retryUncaughtErrors is { } retries && uncaughtErrorCount < retries)
        {
            return CopilotCliExitKind.RetryUncaughtError;
        }

        return CopilotCliExitKind.Failure;
    }

    /// <summary>The hard-failure message: stderr when there is any, else the CLI's <c>session.error</c> message (its failures go to stdout).</summary>
    public static string ErrorMessage(int exitCode, string stderr, string? sessionError = null)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        var significant = SignificantStderr(stderr);
        var detail = significant.Length > 0 ? stderr : sessionError ?? "";
        return $"Error executing copilot cli agent {exitCode}: {detail}";
    }

    [GeneratedRegex(@"^[ \t]*Package extraction took [0-9]+ms[ \t]*(\r?\n|\z)", RegexOptions.Multiline)]
    private static partial Regex StartupNoticeRegex();
}

/// <summary>
/// The bridge-side memory of the Copilot CLI agent, the counterpart of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeStopReasonTracker"/>
/// (the <c>last_stop_reason</c> of inspect_swe's <c>LiveConsumer</c>) extended with the tool calls the bridge
/// returned to the CLI, so a <c>tool.execution_complete</c> line can be attached to the call the model made.
/// The stop reason is reset before every attempt; the tool calls are kept (a resumed session replays them).
/// </summary>
public sealed class CopilotCliBridgeTracker : IModelEventSink
{
    private readonly object _sync = new();

    private readonly Dictionary<string, ToolCall> _toolCalls = new(StringComparer.Ordinal);

    private StopReason? _lastStopReason;

    public StopReason? LastStopReason
    {
        get
        {
            lock (_sync)
            {
                return _lastStopReason;
            }
        }
    }

    /// <summary>The tool call the bridge returned under <paramref name="id"/>, if any.</summary>
    public ToolCall? ToolCall(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_sync)
        {
            return _toolCalls.GetValueOrDefault(id);
        }
    }

    public void OnModelEvent(ModelEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // A failed attempt carries a placeholder output (Python: empty choices), which must not count as completed.
        if (e.Error is not null || e.Output.Choices.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _lastStopReason = e.Output.StopReason;
            foreach (var call in e.Output.Message.ToolCalls ?? [])
            {
                _toolCalls[call.Id] = call;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _lastStopReason = null;
        }
    }
}
