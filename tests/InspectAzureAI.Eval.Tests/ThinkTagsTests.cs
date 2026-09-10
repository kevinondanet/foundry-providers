using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The think-tag helpers of <c>model/_reasoning.py</c> (<c>reasoning_to_think_tag</c>, <c>parse_content_with_reasoning</c>).</summary>
public class ThinkTagsTests
{
    [Fact]
    public void to_think_tag_escapes_the_signature_and_writes_redacted_and_summary()
    {
        var tag = ThinkTags.ToThinkTag(new ContentReasoning("body", "a\"b<c>&'d", Redacted: true) { Summary = "sum" });

        Assert.Equal("<think signature=\"a&quot;b&lt;c&gt;&amp;&#x27;d\" redacted=\"true\">\n<summary>sum</summary>\nbody\n</think>", tag);
        Assert.Equal("<think>\nplain\n</think>", ThinkTags.ToThinkTag(new ContentReasoning("plain")));
    }

    [Theory]
    [InlineData("plan", null, false, null)]
    [InlineData("plan", "sig-1", false, "summary text")]
    [InlineData("ENCRYPTED", "rs_abc", true, "why")]
    [InlineData("multi\nline", "a\"b&c", true, null)]
    public void parse_inverts_to_think_tag(string reasoning, string? signature, bool redacted, string? summary)
    {
        var source = new ContentReasoning(reasoning, signature, redacted) { Summary = summary };

        var (remaining, parsed) = ThinkTags.Parse(ThinkTags.ToThinkTag(source) + "\nAnswer");

        Assert.Equal("Answer", remaining);
        Assert.Equal(source, Assert.Single(parsed));
    }

    [Fact]
    public void parse_is_nesting_aware()
    {
        var (remaining, reasoning) = ThinkTags.Parse("before <think>outer <think>inner</think> tail</think> after");

        Assert.Equal("before  after", remaining);
        Assert.Equal("outer <think>inner</think> tail", Assert.Single(reasoning).Reasoning);
    }

    [Fact]
    public void parse_falls_back_to_the_first_lazy_block_when_unbalanced()
    {
        var (remaining, reasoning) = ThinkTags.Parse("<think>a<think>b</think>");

        Assert.Equal("", remaining);
        Assert.Equal("a<think>b", Assert.Single(reasoning).Reasoning);
    }

    [Fact]
    public void parse_extracts_only_the_first_block()
    {
        var (remaining, reasoning) = ThinkTags.Parse("<think>one</think>mid<think>two</think>");

        Assert.Equal("one", Assert.Single(reasoning).Reasoning);
        Assert.Equal("mid<think>two</think>", remaining);
    }

    [Fact]
    public void parse_unescapes_attributes()
    {
        var (_, reasoning) = ThinkTags.Parse("<think signature=\"x&quot;y&amp;z\" redacted=\"false\">r</think>");

        var item = Assert.Single(reasoning);
        Assert.Equal(("x\"y&z", false), (item.Signature, item.Redacted));
    }

    [Theory]
    [InlineData("rs_123", true, "ABCDEF")]
    [InlineData("sig", true, "AB C\nD EF")]
    [InlineData("rs_123", false, "AB C\nD EF")]
    public void redacted_openai_reasoning_has_its_whitespace_removed(string signature, bool redacted, string expected)
    {
        var tag = $"<think signature=\"{signature}\"{(redacted ? " redacted=\"true\"" : "")}>\nAB C\nD EF\n</think>";

        Assert.Equal(expected, Assert.Single(ThinkTags.Parse(tag).Reasoning).Reasoning);
    }

    [Fact]
    public void summary_is_split_out_and_content_without_a_block_is_unchanged()
    {
        var (remaining, reasoning) = ThinkTags.Parse("  <think>\n<summary>short</summary>\nlong reasoning\n</think>\n\nThe answer  ");

        var item = Assert.Single(reasoning);
        Assert.Equal(("long reasoning", "short"), (item.Reasoning, item.Summary));
        Assert.Equal("The answer", remaining);

        var (unchanged, none) = ThinkTags.Parse("  no tags here  ");
        Assert.Equal("  no tags here  ", unchanged);
        Assert.Empty(none);
    }
}
