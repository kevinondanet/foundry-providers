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

asyncio.run(main())
