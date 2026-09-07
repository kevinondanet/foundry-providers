using Azure.Core;
using Azure.Core.Pipeline;

namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Adds <c>extra-parameters: pass-through</c> when the request does not already carry it. The
/// Azure.AI.Inference SDK sets the header itself on non-streaming calls that carry additional body fields
/// but not on streaming calls, and the model-inference gateway rejects unknown fields by default, so a
/// streamed <c>reasoning_effort</c> or <c>thinking</c> would otherwise fail. Python's azure-ai-inference
/// sends the header on both paths whenever <c>model_extras</c> are present.
/// </summary>
internal sealed class PassThroughExtraParametersPolicy : HttpPipelineSynchronousPolicy
{
    public const string HeaderName = "extra-parameters";

    public override void OnSendingRequest(HttpMessage message)
    {
        if (!message.Request.Headers.Contains(HeaderName))
        {
            message.Request.Headers.Add(HeaderName, "pass-through");
        }
    }
}
