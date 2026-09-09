"""Generate offline wire fixtures with the local Python Inspect providers (no network)."""
import asyncio
import json
import os
from pathlib import Path
import subprocess
from inspect_ai.model import ChatMessageSystem, ChatMessageUser, ChatMessageAssistant, ChatMessageTool, GenerateConfig
from inspect_ai.tool import ToolInfo, ToolParams, ToolParam, ToolCall
from inspect_ai._util.content import ContentText, ContentImage
from inspect_ai.model._providers.anthropic import AnthropicAPI

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = Path(os.environ.get("INSPECT_PROVIDER_FIXTURE_DIR", ROOT / "tests/InspectAzureAI.Tests/Fixtures"))
OUTPUT.mkdir(parents=True, exist_ok=True)
SCHEMA = {"name":"result", "json_schema":{"type":"object", "properties":{"answer":{"type":"string"}}, "required":["answer"], "additionalProperties":False}, "strict":True}
def scenario_inputs(scenario):
    if scenario == "tools_images":
        return [ChatMessageSystem(content="Be brief."), ChatMessageUser(content=[ContentText(text="hi"), ContentImage(image="data:image/png;base64,aGk=")]), ChatMessageAssistant(content="Checking", tool_calls=[ToolCall(id="call1", function="f", arguments={"x":"v"})]), ChatMessageTool(content="done", tool_call_id="call1", function="f")], [ToolInfo(name="f", description="Test function", parameters=ToolParams(properties={"x":ToolParam(type="string")}, required=["x"]))]
    return [ChatMessageSystem(content="Be brief."), ChatMessageUser(content="hi")], []

class Captured(Exception):
    pass

async def anthropic_case(model, config_args, scenario="simple"):
    api = AnthropicAPI(model, api_key="fixture-key", base_url="https://fixture.invalid", streaming=False)
    config = GenerateConfig(cache_prompt=False, **config_args)
    if config.max_tokens is None:
        config.max_tokens = api.max_tokens_for_config(config)
    captured = {}
    async def capture(request, *args, **kwargs):
        captured.update(request)
        raise Captured()
    api._perform_request_and_continuations = capture
    try:
        await api.generate(*scenario_inputs(scenario), "auto", config)
    except Captured:
        pass
    finally:
        await api.aclose()
    headers = captured.pop("extra_headers", {})
    headers = {k: v for k, v in headers.items() if k in ("anthropic-version", "anthropic-beta")}
    captured.update(captured.pop("extra_body", {}))
    return {"provider": "anthropic", "scenario":scenario, "model": model, "config": config_args, "body": captured, "headers": headers}

async def main():
    cases = []
    for model, config in [
        ("claude-3-5-sonnet-latest", {}),
        ("claude-3-7-sonnet-latest", {"reasoning_effort": "high"}),
        ("claude-sonnet-4-5", {"reasoning_effort": "medium"}),
        ("claude-sonnet-4-6", {"reasoning_effort": "high"}),
        ("claude-sonnet-4-6", {"reasoning_tokens": 4096}),
        ("claude-opus-5", {"reasoning_effort": "max"}),
        ("claude-opus-5", {"reasoning_effort": "none", "effort": "max"}),
    ]:
        cases.append(await anthropic_case(model, config))
    cases.append(await anthropic_case("claude-opus-5", {"reasoning_effort":"high"}, "tools_images"))
    cases.append(await anthropic_case("claude-sonnet-4-6", {"response_schema":SCHEMA,"reasoning_effort":"high","reasoning_tokens":2048}, "simple"))
    cases.append(await anthropic_case("claude-opus-4", {"reasoning_effort":"high"}))
    import inspect_ai
    source = Path(inspect_ai.__file__).resolve().parents[2]
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    output = {"python_revision": revision, "notes": ["Caching disabled: provider-native caching is outside this port.", "Transport-only stream=false and request tracing headers omitted."], "cases": cases}
    (OUTPUT / "anthropic-requests.json").write_text(json.dumps(output, indent=2) + "\n")

async def openai_case(model, config_args, model_args, scenario="simple"):
    import httpx
    from inspect_ai.model._providers.openai import OpenAIAPI
    captured = {}
    def capture(request):
        captured["path"] = request.url.path
        captured["body"] = json.loads(request.content)
        captured["headers"] = {k: v for k, v in request.headers.items() if k in ("openai-organization", "openai-project")}
        if request.url.path.endswith("chat/completions"):
            return httpx.Response(200, json={"id":"chat_1", "object":"chat.completion", "created":0, "model":model, "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}], "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}})
        return httpx.Response(200, json={"id":"resp_1", "object":"response", "model":model,"status":"completed", "output":[], "usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}, "created_at":0})
    client = httpx.AsyncClient(transport=httpx.MockTransport(capture))
    config = GenerateConfig(**config_args)
    api = OpenAIAPI(model, api_key="fixture-key", base_url="https://fixture.invalid/v1", http_client=client, streaming=False, config=config, **model_args)
    api._reasoning_summaries = False  # deliberate opt-in summaries policy; no live capability probe
    try:
        await api.generate(*scenario_inputs(scenario), "auto", config)
    finally:
        await api.aclose()
    return {"provider":"openai", "scenario":scenario, "model":model, "config":config_args, "model_args":model_args, **captured}

async def openai_main(chat=False):
    cases = []
    for model, config, args in ([
        ("gpt-4", {"max_tokens":50,"temperature":0.2,"top_p":0.8,"num_choices":2,"stop_seqs":["STOP"],"frequency_penalty":0.1,"presence_penalty":0.2,"logprobs":True,"top_logprobs":2,"seed":17}, {}),
        ("gpt-5.6-sol", {"num_choices":1,"max_tokens":100,"reasoning_effort":"max","temperature":0.2}, {}),
        ("gpt-5.4-mini", {}, {"responses_api":False}),
    ] if chat else [
        ("gpt-5.6-sol", {}, {}),
        ("gpt-5.4-mini", {"reasoning_effort":"max", "temperature":0.2}, {}),
        ("gpt-5.6-sol", {"reasoning_effort":"max", "reasoning_summary":"detailed"}, {}),
        ("gpt-4", {"temperature":0.3}, {"responses_api":True}),
        ("gpt-5.6-sol", {"num_choices":1}, {"responses_api":True}),
        ("gpt-5.4-pro", {}, {}),
        ("o3-deep-research", {}, {}),
        ("gpt-5.6-sol", {}, {"responses_store":True, "organization":"org-fixture", "project":"proj-fixture", "safety_identifier":"safe-fixture"}),
    ]):
        cases.append(await openai_case(model, config, args))
    cases.append(await openai_case("gpt-5.6-sol", {"max_tokens":100,"parallel_tool_calls":False}, {"responses_api": not chat}, "tools_images"))
    cases.append(await openai_case("gpt-5.6-sol", {"response_schema":SCHEMA}, {"responses_api": not chat}))
    import inspect_ai
    revision = subprocess.check_output(["git", "-C", str(Path(inspect_ai.__file__).resolve().parents[2]), "rev-parse", "HEAD"], text=True).strip()
    output = {"python_revision":revision, "notes":["Reasoning summaries disabled unless explicit: no live verification probe.", "Responses store=true is sent explicitly by C#, equivalent to the Python SDK default."], "cases":cases}
    (OUTPUT / ("openai-chat-requests.json" if chat else "openai-responses-requests.json")).write_text(json.dumps(output, indent=2) + "\n")

if __name__ == "__main__":
    asyncio.run(main())
    asyncio.run(openai_main())
    asyncio.run(openai_main(chat=True))
