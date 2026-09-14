using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary><see cref="ShellQuote"/> against Python's <c>shlex.quote</c> / <c>shlex.join</c> outputs.</summary>
public class ShellQuoteTests
{
    [Theory]
    [InlineData("", "''")]
    [InlineData("plain", "plain")]
    [InlineData("a/b.c:d,e=f+g%h@i_j-k", "a/b.c:d,e=f+g%h@i_j-k")]
    [InlineData("two words", "'two words'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("web_search=\"live\"", "'web_search=\"live\"'")]
    [InlineData("a\nb", "'a\nb'")]
    [InlineData("café", "'café'")]
    [InlineData("*", "'*'")]
    public void quote_matches_python_shlex(string arg, string expected)
    {
        Assert.Equal(expected, ShellQuote.Quote(arg));
    }

    [Fact]
    public void join_quotes_each_argument_and_separates_with_spaces()
    {
        Assert.Equal(
            "codex exec --model gpt-5.4 -c 'web_search=\"live\"' 'it'\"'\"'s ok' ''",
            ShellQuote.Join(["codex", "exec", "--model", "gpt-5.4", "-c", "web_search=\"live\"", "it's ok", ""]));
        Assert.Equal("", ShellQuote.Join([]));
    }
}
