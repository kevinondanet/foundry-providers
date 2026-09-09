using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tools/_web_browser/_web_browser.py</c> <c>WebBrowserStore</c>: the per-sample (and
/// per-instance) state of a web browser session, written to the sample <see cref="Store"/> under the same
/// keys Python uses (<c>WebBrowserStore[:{instance}]:{field}</c>) so logs read the same on both sides.
/// </summary>
public sealed class WebBrowserStore : StoreModel
{
    /// <summary>The main text content of the last page, or "(no main text summary)".</summary>
    public string MainContent
    {
        get => Get("");
        set => Set(value);
    }

    /// <summary>The last web accessibility tree (base64 image data included, unlike the tool result).</summary>
    public string WebAt
    {
        get => Get("");
        set => Set(value);
    }

    /// <summary>The <c>session_name</c> the in-sandbox browser service issued; empty until the first call.</summary>
    public string SessionId
    {
        get => Get("");
        set => Set(value);
    }
}
