using System.Text;

namespace InspectAzureAI.Eval.Context;

/// <summary>The reader and writer an <see cref="InputScreen"/> talks to; <see cref="System"/> is the process console.</summary>
public sealed record InputConsole(TextReader Input, TextWriter Output)
{
    /// <summary><c>Console.In</c> / <c>Console.Out</c>, resolved when called.</summary>
    public static InputConsole System() => new(Console.In, Console.Out);
}

/// <summary>
/// Port of <c>util/_console.py</c> <c>input_screen</c> and the rich display's <c>input_screen</c>
/// (<c>_display/rich/display.py</c>): a scope for receiving console input that prints an optional header, records
/// everything shown and typed while it is open, and on close writes that recording to the sample transcript as an
/// <see cref="InputEvent"/> (unless the caller emits its own structured event). Deviation: the port's console
/// reporter has no live progress region to clear, so nothing is paused or restored; <c>transient</c> and <c>width</c>
/// have no equivalent; the recording is plain text (<c>input</c> and <c>input_ansi</c> are the same string). The console
/// is replaceable through <see cref="DefaultConsole"/> so tests and <c>--fake</c> hosts can script the operator.
/// </summary>
public static class InputScreen
{
    /// <summary>The console <see cref="Open"/> uses when none is given; null (the default) means the process console.</summary>
    public static InputConsole? DefaultConsole { get; set; }

    /// <summary>
    /// Opens an input screen: prints <c>── header ──</c> and a blank line when a header is given and returns the
    /// <see cref="ConsoleInput"/> to prompt with. Dispose it when the input is complete: with
    /// <paramref name="recordEvent"/> the recorded session becomes an <see cref="InputEvent"/> in the current
    /// sample's transcript (no-op outside a sample), and a blank line is printed.
    /// </summary>
    public static ConsoleInput Open(string? header = null, bool recordEvent = true, InputConsole? console = null)
    {
        var resolved = console ?? DefaultConsole ?? InputConsole.System();
        var screen = new ConsoleInput(resolved, recordEvent ? SampleContext.Current?.Transcript : null);
        if (header is not null)
        {
            screen.Print($"── {header} ──");
            screen.Print();
        }

        return screen;
    }
}

/// <summary>
/// The console yielded by <see cref="InputScreen.Open"/>: <see cref="Ask"/> is rich's <c>Prompt.ask</c> (the prompt,
/// the default in parentheses when shown, then <c>: </c>; an empty answer takes the default). Everything printed and
/// every answer is captured in <see cref="Recorded"/> (rich's console recording).
/// </summary>
public sealed class ConsoleInput : IDisposable
{
    private readonly InputConsole _console;

    private readonly Transcript? _transcript;

    private readonly StringBuilder _record = new();

    private readonly RecordingWriter _writer;

    private bool _disposed;

    internal ConsoleInput(InputConsole console, Transcript? transcript)
    {
        _console = console;
        _transcript = transcript;
        _writer = new RecordingWriter(console.Output, _record);
    }

    /// <summary>A writer that echoes to the console and into <see cref="Recorded"/>.</summary>
    public TextWriter Writer => _writer;

    /// <summary>The console's reader (lines read through it directly are not recorded; use <see cref="ReadLine"/>).</summary>
    public TextReader Reader => _console.Input;

    /// <summary>The console session so far: everything printed plus the answers typed.</summary>
    public string Recorded => _record.ToString();

    public void Print(string text = "") => _writer.WriteLine(text);

    /// <summary>Port of <c>Prompt.ask(prompt, default=..., show_default=...)</c>. End of input is an <see cref="EndOfStreamException"/>.</summary>
    public string Ask(string prompt, string? @default = null, bool showDefault = true)
    {
        _writer.Write(PromptText(prompt, @default, showDefault));
        var line = ReadLine() ?? throw new EndOfStreamException("Console input ended before the prompt was answered.");
        return line.Length == 0 && @default is not null ? @default : line;
    }

    /// <summary><see cref="Ask"/> reading asynchronously (honouring <paramref name="cancellationToken"/> when the reader does).</summary>
    public async Task<string> AskAsync(string prompt, string? @default = null, bool showDefault = true, CancellationToken cancellationToken = default)
    {
        await _writer.WriteAsync(PromptText(prompt, @default, showDefault)).ConfigureAwait(false);
        var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Console input ended before the prompt was answered.");
        return line.Length == 0 && @default is not null ? @default : line;
    }

    /// <summary>Reads one line from the console, recording it (null at end of input).</summary>
    public string? ReadLine() => Record(_console.Input.ReadLine());

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        Record(await _console.Input.ReadLineAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Records the session as an <see cref="InputEvent"/> (when opened with <c>recordEvent</c> inside a sample) and prints a blank line.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var text = Recorded;
        _transcript?.Add(new InputEvent(text, text));
        _console.Output.WriteLine();
        _console.Output.Flush();
    }

    private string? Record(string? line)
    {
        if (line is not null)
        {
            _record.Append(line).Append('\n');
        }

        return line;
    }

    private static string PromptText(string prompt, string? @default, bool showDefault)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return @default is not null && showDefault ? $"{prompt} ({@default}): " : $"{prompt}: ";
    }

    private sealed class RecordingWriter(TextWriter inner, StringBuilder record) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            record.Append(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            record.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            inner.Write(buffer, index, count);
            record.Append(buffer, index, count);
        }

        public override void WriteLine() => Write(CoreNewLine);

        public override void WriteLine(string? value)
        {
            Write(value);
            WriteLine();
        }

        public override void Flush() => inner.Flush();

        protected override void Dispose(bool disposing)
        {
            // the console outlives the screen; only flush
            if (disposing)
            {
                inner.Flush();
            }
        }
    }
}
