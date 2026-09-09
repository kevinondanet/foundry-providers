using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.InlineCards;

/// <summary>The choices of the inline cancel card of <c>examples/inline_cards/cancel_sample.py</c> (<c>^N</c>): Score / Error / Back.</summary>
public enum CancelResolution
{
    /// <summary>End the sample cleanly; scoring runs on whatever the agent has produced so far.</summary>
    Score,

    /// <summary>End the sample as an error.</summary>
    Error,

    /// <summary>Dismiss the card and let the agent continue.</summary>
    Back,
}

/// <summary>
/// The operator behind the <c>^L</c> (cancel tool call) and <c>^N</c> (cancel sample) keybindings of the Textual/ACP
/// TUI that <c>cancel_tool.py</c> and <c>cancel_sample.py</c> demonstrate. Deviation: the TUI and its inline
/// <c>_CancelCard</c> are not ported; the tools of this example watch an <see cref="IOperator"/> while they sleep —
/// the console (<see cref="ConsoleOperator"/>: Enter, then a Score/Error/Back prompt) or, under <c>--fake</c>, a
/// script (<see cref="ScriptedOperator"/>: a timer and a list of resolutions).
/// </summary>
public interface IOperator
{
    /// <summary>Completes when the operator cancels the running <paramref name="toolCall"/> (<c>^L</c>); never, if they do not.</summary>
    Task WaitForCancelToolCallAsync(string toolCall, CancellationToken cancellationToken);

    /// <summary>Completes with the resolution the operator picks on the cancel card (<c>^N</c>) while <paramref name="toolCall"/> runs; never, if they do not open it.</summary>
    Task<CancelResolution> WaitForCancelSampleAsync(string toolCall, CancellationToken cancellationToken);
}

/// <summary>
/// The console operator: Enter at the terminal stands for <c>^L</c> / <c>^N</c>, and the cancel card is the prompt
/// <c>Score (s), Error (e), or Back (b) [s/e/b] (b):</c> (an empty line takes Back, an unknown answer re-prompts as
/// rich's <c>Prompt.ask</c> does). End of input leaves the tool running.
/// </summary>
public sealed class ConsoleOperator(TextReader? input = null, TextWriter? output = null) : IOperator
{
    public const string InvalidChoice = "Please select one of the available options";

    private readonly TextReader _input = input ?? Console.In;
    private readonly TextWriter _output = output ?? Console.Out;

    public async Task WaitForCancelToolCallAsync(string toolCall, CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync($"[operator] {toolCall} is running: press Enter to cancel the tool call (the TUI's ^L)").ConfigureAwait(false);
        await ReadLineOrWaitForeverAsync(cancellationToken).ConfigureAwait(false);
        await _output.WriteLineAsync($"[operator] cancelling {toolCall}").ConfigureAwait(false);
    }

    public async Task<CancelResolution> WaitForCancelSampleAsync(string toolCall, CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync($"[operator] {toolCall} is running: press Enter to open the cancel card (the TUI's ^N)").ConfigureAwait(false);
        await ReadLineOrWaitForeverAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            await _output.WriteAsync("Score (s), Error (e), or Back (b) [s/e/b] (b): ").ConfigureAwait(false);
            var line = await ReadLineOrWaitForeverAsync(cancellationToken).ConfigureAwait(false);
            switch (line.Trim().ToLowerInvariant())
            {
                case "" or "b" or "back":
                    return CancelResolution.Back;
                case "s" or "score":
                    return CancelResolution.Score;
                case "e" or "error":
                    return CancelResolution.Error;
                default:
                    await _output.WriteLineAsync(InvalidChoice).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task<string> ReadLineOrWaitForeverAsync(CancellationToken cancellationToken)
    {
        var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null)
        {
            return line;
        }

        // No terminal (or it closed): the operator never interrupts, exactly as if they never pressed the key.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new OperationCanceledException(cancellationToken);
    }
}

/// <summary>
/// The operator under <c>--fake</c> (and in tests): presses <c>^L</c> / <c>^N</c> <paramref name="delay"/> after the tool
/// starts, and picks <paramref name="resolutions"/> on the cancel card in order (Back leaves the tool running until
/// the next entry fires, after another <paramref name="delay"/>; once the list is exhausted the card is never opened
/// again). Every choice is printed to <paramref name="output"/> and kept in <see cref="Resolutions"/>.
/// </summary>
public sealed class ScriptedOperator(TimeSpan delay, IReadOnlyList<CancelResolution>? resolutions = null, TextWriter? output = null) : IOperator
{
    private readonly object _sync = new();
    private readonly Queue<CancelResolution> _resolutions = new(resolutions ?? [CancelResolution.Score]);
    private readonly List<CancelResolution> _picked = [];
    private readonly TextWriter _output = output ?? Console.Out;

    /// <summary>How many times the tool-call cancel (<c>^L</c>) fired.</summary>
    public int ToolCallCancels { get; private set; }

    /// <summary>The resolutions picked so far, in order.</summary>
    public IReadOnlyList<CancelResolution> Resolutions
    {
        get
        {
            lock (_sync)
            {
                return _picked.ToArray();
            }
        }
    }

    public async Task WaitForCancelToolCallAsync(string toolCall, CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync($"[operator] {toolCall} is running: cancelling it in {delay.TotalSeconds:0.##}s (scripted ^L)").ConfigureAwait(false);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            ToolCallCancels++;
        }

        await _output.WriteLineAsync($"[operator] cancelling {toolCall} (scripted)").ConfigureAwait(false);
    }

    public async Task<CancelResolution> WaitForCancelSampleAsync(string toolCall, CancellationToken cancellationToken)
    {
        CancelResolution? next;
        lock (_sync)
        {
            next = _resolutions.Count > 0 ? _resolutions.Dequeue() : null;
        }

        if (next is not { } resolution)
        {
            // The script ran dry (after a Back): the operator never opens the card again, the tool runs to completion.
            await _output.WriteLineAsync($"[operator] {toolCall} keeps running (no more scripted resolutions)").ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }

        await _output.WriteLineAsync($"[operator] {toolCall} is running: opening the cancel card in {delay.TotalSeconds:0.##}s (scripted ^N)").ConfigureAwait(false);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _picked.Add(resolution);
        }

        await _output.WriteLineAsync($"[operator] cancel card: Score / Error / Back -> {resolution} (scripted)").ConfigureAwait(false);
        return resolution;
    }

    /// <summary>Parses a <c>-T resolution=score,error,back</c> list (case-insensitive); an unknown word is an <see cref="ArgumentException"/>.</summary>
    public static IReadOnlyList<CancelResolution> ParseResolutions(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var resolutions = new List<CancelResolution>();
        foreach (var word in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            resolutions.Add(Enum.TryParse<CancelResolution>(word, ignoreCase: true, out var resolution)
                ? resolution
                : throw new ArgumentException($"-T resolution expects score, error or back (comma-separated), got '{word}'"));
        }

        return resolutions.Count > 0 ? resolutions : throw new ArgumentException("-T resolution expects at least one of score, error or back");
    }
}

/// <summary>The Error resolution of the cancel card: ends the sample as an error (the runner records it as the sample's error).</summary>
public sealed class OperatorCancelledException(string message) : Exception(message);

/// <summary>What the two cancel demos record and how they wait: the port of the engine-side cancellation Python's TUI drives.</summary>
internal static class OperatorCancellation
{
    /// <summary>Port of the interrupt event Python records for an operator cancellation: source <c>user_cancel</c>, interrupting <c>tool_call</c>, with the id of the running call when the sample state shows it.</summary>
    public static void RecordInterrupt(string toolCall)
    {
        var context = SampleContext.Current;
        if (context is null)
        {
            return;
        }

        // The react agent works on its own copy of the conversation, so the running call is found on the last model
        // event of the transcript (falling back to the sample state for a plain solver).
        var assistant = context.Transcript.Events.OfType<InspectAzureAI.Eval.Model.ModelEvent>().LastOrDefault()?.Output.Message
            ?? context.SampleState?.Messages.OfType<ChatMessageAssistant>().LastOrDefault();
        var id = assistant?.ToolCalls?.LastOrDefault(call => call.Function == toolCall)?.Id;
        context.Transcript.Add(new InterruptEvent("user_cancel", "tool_call") { InterruptedToolCallId = id });
    }

    /// <summary>Lets a raced task's cancellation or fault go unobserved without noise.</summary>
    public static void Observe(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
