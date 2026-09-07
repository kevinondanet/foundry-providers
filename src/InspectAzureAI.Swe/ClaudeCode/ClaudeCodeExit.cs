using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>How an attempt's exit code is handled (port of <c>claude_code.py:574-603</c>).</summary>
public enum ClaudeCodeExitKind
{
    /// <summary>Exit code 0.</summary>
    Success,

    /// <summary>Exit 1 after an Anthropic refusal: the refusal is already in the bridge state and scorable.</summary>
    Refusal,

    /// <summary>Exit 1 with empty stderr and retries left: an uncaught exception reached Claude Code's main loop.</summary>
    RetryUncaughtError,

    /// <summary>Anything else: a hard failure.</summary>
    Failure,
}

/// <summary>Port of the exit-code rules of inspect_swe <c>_claude_code/claude_code.py</c> (<c>_is_claude_code_refusal_exit</c> and the retry rule at <c>:583-597</c>).</summary>
public static class ClaudeCodeExit
{
    /// <summary>Port of <c>_is_claude_code_refusal_exit</c>: exit 1, no stderr, and the last bridged generation stopped with <c>content_filter</c> (Inspect's mapping of an Anthropic refusal).</summary>
    public static bool IsRefusalExit(int exitCode, string stderr, StopReason? lastStopReason)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        if (exitCode != 1 || stderr.Trim().Length > 0)
        {
            return false;
        }

        return lastStopReason == StopReason.ContentFilter;
    }

    public static ClaudeCodeExitKind Classify(int exitCode, string stderr, StopReason? lastStopReason, int? retryUncaughtErrors, int uncaughtErrorCount)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        if (exitCode == 0)
        {
            return ClaudeCodeExitKind.Success;
        }

        if (IsRefusalExit(exitCode, stderr, lastStopReason))
        {
            return ClaudeCodeExitKind.Refusal;
        }

        if (exitCode == 1 && stderr.Trim().Length == 0 && retryUncaughtErrors is { } retries && uncaughtErrorCount < retries)
        {
            return ClaudeCodeExitKind.RetryUncaughtError;
        }

        return ClaudeCodeExitKind.Failure;
    }

    /// <summary>The hard-failure message of <c>claude_code.py:599</c>.</summary>
    public static string ErrorMessage(int exitCode, string stderr) => $"Error executing claude code agent {exitCode}: {stderr}";
}

/// <summary>
/// The <c>last_stop_reason</c> part of inspect_swe <c>_claude_code/_events/live_consumer.py</c>: installed as the
/// bridge's model event sink, it remembers the stop reason of the latest completed generation so a refusal exit
/// can be told from a crash. Reset before every attempt, as <c>LiveConsumer.reset()</c> is.
/// </summary>
public sealed class ClaudeCodeStopReasonTracker : IModelEventSink
{
    private readonly object _sync = new();

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
