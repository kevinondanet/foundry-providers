using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Tests;

/// <summary><c>input_screen</c> (<c>util/_console.py</c> and the rich display's <c>input_screen</c>) as a console-only scope.</summary>
public sealed class InputScreenTests
{
    private static InputConsole Console(StringWriter output, params string[] lines) =>
        new(new StringReader(string.Join("\n", lines) + "\n"), output);

    [Fact]
    public void the_screen_prints_the_header_prompts_and_records_an_input_event()
    {
        using var scope = new SampleContextScope();
        var output = new StringWriter();
        string answer;
        using (var screen = InputScreen.Open("User Prompt", console: Console(output, "List the files in /tmp")))
        {
            answer = screen.Ask("Please enter your initial prompt for the model:\n\n");
        }

        Assert.Equal("List the files in /tmp", answer);
        Assert.Equal("── User Prompt ──\n\nPlease enter your initial prompt for the model:\n\n: \n", output.ToString());
        var input = Assert.Single(scope.Transcript.Events.OfType<InputEvent>());
        Assert.Equal("── User Prompt ──\n\nPlease enter your initial prompt for the model:\n\n: List the files in /tmp\n", input.Input);
        Assert.Equal(input.Input, input.InputAnsi);
        Assert.Null(input.Message);
        Assert.Null(input.Outcome);
    }

    [Fact]
    public async Task an_empty_answer_takes_the_default_which_is_shown_in_parentheses()
    {
        using var scope = new SampleContextScope();
        var output = new StringWriter();
        using var screen = InputScreen.Open("Next Action", console: Console(output, "", "exit", ""));

        var first = await screen.AskAsync("Type a message or 'exit'", @default: "");
        var second = screen.Ask("Again", @default: "continue");
        var hidden = screen.Ask("Hidden", @default: "x", showDefault: false);

        Assert.Equal("", first);
        Assert.Equal("exit", second);
        Assert.Contains("Type a message or 'exit' (): ", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Again (continue): ", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Hidden: ", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("x", hidden);
    }

    [Fact]
    public void record_event_false_or_no_sample_records_nothing()
    {
        var output = new StringWriter();
        using (var scope = new SampleContextScope())
        {
            using (var screen = InputScreen.Open(recordEvent: false, console: Console(output, "a")))
            {
                Assert.Equal("a", screen.Ask("q"));
            }

            Assert.DoesNotContain(scope.Transcript.Events, e => e is InputEvent);
        }

        // outside a sample there is no transcript to record into; the screen still works
        using (var screen = InputScreen.Open("Header", console: Console(output, "b")))
        {
            screen.Print("hello");
            Assert.Equal("b", screen.Ask("q"));
            Assert.Equal("── Header ──\n\nhello\nq: b\n", screen.Recorded);
        }

        Assert.EndsWith("q: \n", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_default_console_seam_supplies_the_reader_and_writer()
    {
        var previous = InputScreen.DefaultConsole;
        try
        {
            var output = new StringWriter();
            InputScreen.DefaultConsole = Console(output, "scripted");
            using var screen = InputScreen.Open();
            Assert.Equal("scripted", screen.Ask("prompt"));
            Assert.Equal("prompt: ", output.ToString());
        }
        finally
        {
            InputScreen.DefaultConsole = previous;
        }
    }

    [Fact]
    public void end_of_input_is_an_end_of_stream_exception()
    {
        using var screen = InputScreen.Open(console: new InputConsole(new StringReader(""), new StringWriter()));

        Assert.Throws<EndOfStreamException>(() => screen.Ask("q"));
        Assert.Null(screen.ReadLine());
    }
}
