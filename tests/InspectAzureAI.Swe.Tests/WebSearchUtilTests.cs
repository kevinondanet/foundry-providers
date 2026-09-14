using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Port of inspect_swe <c>tests/test_websearch.py</c>.</summary>
public class WebSearchUtilTests
{
    [Fact]
    public void tool_disallowed_matches_bare_name()
    {
        Assert.True(WebSearchUtil.ToolDisallowed(["WebSearch"], "WebSearch"));
        Assert.False(WebSearchUtil.ToolDisallowed(["WebFetch"], "WebSearch"));
        Assert.False(WebSearchUtil.ToolDisallowed(null, "WebSearch"));
        Assert.False(WebSearchUtil.ToolDisallowed([], "WebSearch"));
    }

    [Fact]
    public void tool_disallowed_matches_scoped_form()
    {
        Assert.True(WebSearchUtil.ToolDisallowed(["WebFetch(domain:example.com)"], "WebFetch"));
        Assert.False(WebSearchUtil.ToolDisallowed(["WebSearchOther"], "WebSearch"));
    }

    [Fact]
    public void matching_is_exact_and_case_sensitive()
    {
        Assert.True(WebSearchUtil.ToolDisallowed(["Bash", "WebSearch(x)"], "WebSearch"));
        Assert.False(WebSearchUtil.ToolDisallowed(["websearch"], "WebSearch"));
        Assert.False(WebSearchUtil.ToolDisallowed(["Web"], "WebSearch"));
        Assert.False(WebSearchUtil.ToolDisallowed([" WebSearch"], "WebSearch"));
    }
}
