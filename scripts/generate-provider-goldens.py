"""Generate offline wire fixtures with the local Python Inspect providers (no network)."""
import asyncio
import json
from pathlib import Path
import subprocess
from inspect_ai.model import ChatMessageSystem, ChatMessageUser, GenerateConfig
from inspect_ai.model._providers.anthropic import AnthropicAPI

ROOT = Path(__file__).resolve().parents[1]
class Captured(Exception):
    pass

async def anthropic_case(model, config_args):
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
        await api.generate([ChatMessageSystem(content="Be brief."), ChatMessageUser(content="hi")], [], "auto", config)
    except Captured:
        pass
    finally:
        await api.aclose()
    headers = captured.pop("extra_headers", {})
    headers = {k: v for k, v in headers.items() if k in ("anthropic-version", "anthropic-beta")}
    captured.update(captured.pop("extra_body", {}))
    return {"provider": "anthropic", "model": model, "config": config_args, "body": captured, "headers": headers}

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
    import inspect_ai
    source = Path(inspect_ai.__file__).resolve().parents[2]
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    output = {"python_revision": revision, "notes": ["Caching disabled: provider-native caching is outside this port.", "Transport-only stream=false and request tracing headers omitted."], "cases": cases}
    (ROOT / "tests/InspectAzureAI.Tests/Fixtures/anthropic-requests.json").write_text(json.dumps(output, indent=2) + "\n")

async def openai_case(model, config_args, model_args):
    import httpx
    from inspect_ai.model._providers.openai import OpenAIAPI
    captured = {}
    def capture(request):
        captured["body"] = json.loads(request.content)
        captured["headers"] = {k: v for k, v in request.headers.items() if k in ("openai-organization", "openai-project")}
        return httpx.Response(200, json={"id":"resp_1", "object":"response", "model":model,"status":"completed", "output":[], "usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}, "created_at":0})
    client = httpx.AsyncClient(transport=httpx.MockTransport(capture))
    config = GenerateConfig(**config_args)
    api = OpenAIAPI(model, api_key="fixture-key", base_url="https://fixture.invalid/v1", http_client=client, streaming=False, config=config, **model_args)
    api._reasoning_summaries = False  # deliberate opt-in summaries policy; no live capability probe
    try:
        await api.generate([ChatMessageSystem(content="Be brief."), ChatMessageUser(content="hi")], [], "auto", config)
    finally:
        await api.aclose()
    return {"provider":"openai", "model":model, "config":config_args, "model_args":model_args, **captured}

async def openai_main():
    cases = []
    for model, config, args in [
        ("gpt-5.6-sol", {}, {}),
        ("gpt-5.4-mini", {"reasoning_effort":"max", "temperature":0.2}, {}),
        ("gpt-5.6-sol", {"reasoning_effort":"max", "reasoning_summary":"detailed"}, {}),
        ("gpt-4", {"temperature":0.3}, {"responses_api":True}),
        ("gpt-5.6-sol", {"num_choices":1}, {"responses_api":True}),
        ("gpt-5.4-pro", {}, {}),
        ("o3-deep-research", {}, {}),
        ("gpt-5.6-sol", {}, {"responses_store":True, "organization":"org-fixture", "project":"proj-fixture", "safety_identifier":"safe-fixture"}),
    ]:
        cases.append(await openai_case(model, config, args))
    import inspect_ai
    revision = subprocess.check_output(["git", "-C", str(Path(inspect_ai.__file__).resolve().parents[2]), "rev-parse", "HEAD"], text=True).strip()
    output = {"python_revision":revision, "notes":["Reasoning summaries disabled unless explicit: no live verification probe.", "Responses store=true is sent explicitly by C#, equivalent to the Python SDK default."], "cases":cases}
    (ROOT / "tests/InspectAzureAI.Tests/Fixtures/openai-responses-requests.json").write_text(json.dumps(output, indent=2) + "\n")

if __name__ == "__main__":
    asyncio.run(main())
    asyncio.run(openai_main())
