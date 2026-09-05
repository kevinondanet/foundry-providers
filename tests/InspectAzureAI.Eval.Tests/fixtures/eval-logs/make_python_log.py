"""Generates fixtures/eval-logs/python_eval_log.json: a small synthetic EvalLog covering every event type,
written with inspect_ai.log.write_eval_log so the C# reader is tested against the real Python format.

Regenerate with the inspect_ai venv:
    python tests/InspectAzureAI.Eval.Tests/fixtures/eval-logs/make_python_log.py tests/InspectAzureAI.Eval.Tests/fixtures/eval-logs/python_eval_log.json
"""
import sys
from datetime import datetime, timezone
from typing import get_args

from inspect_ai._util.error import EvalError
from inspect_ai._util.json import JsonChange
from inspect_ai.dataset import Sample
from inspect_ai.event import (
    AnchorEvent, ApprovalEvent, BranchEvent, CompactionEvent, ErrorEvent,
    InfoEvent, InputEvent, InterruptEvent, LoggerEvent, ModelEvent, SampleInitEvent,
    SampleLimitEvent, SandboxEvent, ScoreEvent, ScoreEditEvent, SpanBeginEvent, SpanEndEvent,
    StateEvent, StepEvent, StoreEvent, SubtaskEvent, ToolEvent,
)
from inspect_ai.event import timeline_build
from inspect_ai.event._timeline import Outline, OutlineNode
from inspect_ai.event._input import InputField
from inspect_ai.event._checkpoint import CheckpointEvent
from inspect_ai.event._logger import LoggingMessage
from inspect_ai.log import (
    EvalConfig, EvalDataset, EvalLog, EvalMetric, EvalPlan, EvalPlanStep, EvalResults,
    EvalRevision, EvalSample, EvalSampleLimit, EvalSampleReductions, EvalSampleScore, EvalScore,
    EvalSpec, EvalStats, write_eval_log,
)
from inspect_ai.log._edit import LogUpdate, MetadataEdit, ProvenanceData, TagsEdit
from inspect_ai.model import (
    ChatCompletionChoice, ChatMessageAssistant, ChatMessageSystem, ChatMessageTool, ChatMessageUser,
    GenerateConfig, ModelCall, ModelOutput, ModelUsage,
)
from inspect_ai._util.content import ContentText, ContentReasoning, ContentImage
from inspect_ai.scorer import Score
from inspect_ai.scorer._metric import ScoreEdit
from inspect_ai.tool import ToolCall, ToolCallError, ToolInfo
from inspect_ai.tool._tool_call import ToolCallContent, ToolCallView
from inspect_ai.tool._tool_params import ToolParams, ToolParam
from inspect_ai.util import SandboxEnvironmentSpec
from inspect_ai.util._checkpoint._layout.schemas import Checkpoint, CheckpointTriggerKind, SnapshotDetails

T0 = datetime(2026, 9, 4, 10, 30, 0, tzinfo=timezone.utc)

def ts(seconds: float) -> datetime:
    return datetime(2026, 9, 4, 10, 30, int(seconds), int((seconds % 1) * 1_000_000), tzinfo=timezone.utc)

def base(i: int) -> dict:
    return dict(uuid=f"ev-{i:02d}", timestamp=ts(i), working_start=float(i), span_id="span-solver" if i > 1 else None)

tool_call = ToolCall(id="call_1", function="bash", arguments={"cmd": "ls"})
assistant = ChatMessageAssistant(
    id="m3", content=[ContentReasoning(reasoning="let me look", signature="sig", redacted=False), ContentText(text="Listing files.")],
    tool_calls=[tool_call], model="gpt", source="generate",
)
final = ChatMessageAssistant(id="m5", content="The answer is 4.", model="gpt", source="generate")
output = ModelOutput(
    model="gpt", choices=[ChatCompletionChoice(message=final, stop_reason="stop")],
    usage=ModelUsage(input_tokens=10, output_tokens=5, total_tokens=15, input_tokens_cache_read=2), time=0.5,
)
call = ModelCall.create(
    request={"messages": [{"role": "user", "content": "hi"}]},
    response={"id": "resp_1", "usage": {"total_tokens": 15}},
    time=0.5,
)
bash_tool = ToolInfo(
    name="bash", description="Use this function to execute bash commands.",
    parameters=ToolParams(properties={"cmd": ToolParam(type="string", description="The bash command to execute.")}, required=["cmd"]),
)

events = [
    SampleInitEvent(**base(0), sample=Sample(id=1, input="What is 2+2?", target="4", metadata={"difficulty": 1}), state={"messages": [], "store": {}}),
    SpanBeginEvent(**base(1), id="span-solver", parent_id=None, type="solver", name="generate"),
    StepEvent(**base(2), action="begin", type="solver", name="generate"),
    StateEvent(**base(3), changes=[JsonChange(op="add", path="/messages/0", value={"role": "user", "content": "What is 2+2?"}), JsonChange(op="replace", path="/output/completion", value="4", replaced="")]),
    StoreEvent(**base(4), changes=[JsonChange(op="add", path="/steps", value=3)]),
    ModelEvent(**base(5), model="gpt", role="grader", input=[ChatMessageUser(id="m2", content="What is 2+2?", source="input")], tools=[bash_tool], tool_choice="auto", config=GenerateConfig(max_tokens=100, temperature=0.2), output=output, call=call, retries=0, completed=ts(6), working_time=0.5, cache=None),
    ModelEvent(**base(6), model="gpt", input=[], tools=[], tool_choice="none", config=GenerateConfig(), output=ModelOutput(), error="rate limited", retries=1),
    ToolEvent(**base(7), id="call_1", function="bash", arguments={"cmd": "ls"}, view=ToolCallContent(title="bash", format="markdown", content="```\nls\n```"), result="a.txt\nb.txt", truncated=(20000, 16384), error=ToolCallError(type="timeout", message="Command timed out before completing."), completed=ts(8), working_time=1.25, agent=None, failed=True, message_id="m4"),
    ToolEvent(**base(8), id="call_2", function="think", arguments={}, result=[ContentText(text="thought")], completed=ts(9), working_time=0.1),
    ApprovalEvent(**base(9), message="run ls", call=tool_call, view=ToolCallView(call=ToolCallContent(format="text", content="ls")), approver="human", decision="approve", explanation="fine"),
    SandboxEvent(**base(10), action="exec", cmd="ls", options={"timeout": 30}, input=None, result=0, output="a.txt\nb.txt", completed=ts(11)),
    SandboxEvent(**base(11), action="write_file", file="/tmp/x.txt", input="hello"),
    SubtaskEvent(**base(12), name="helper", type="subtask", input={"x": 1}, result={"y": 2}, completed=ts(13), working_time=0.2),
    CompactionEvent(**base(13), type="summary", role=None, tokens_before=1000, tokens_after=200, source="inspect"),
    LoggerEvent(**base(14), message=LoggingMessage(name="httpx", level="info", message="GET /x 200", created=1.757e12, filename="_client.py", module="_client", lineno=42)),
    InputEvent(**base(15), input="yes", input_ansi="\x1b[1myes\x1b[0m", message="Continue?", fields=[InputField(name="confirm", type="boolean", description="Proceed")], outcome="accepted", content={"confirm": True}),
    AnchorEvent(**base(16), anchor_id="anchor-1", source="mod.fn"),
    BranchEvent(**base(17), from_anchor="anchor-1"),
    CheckpointEvent.from_details(Checkpoint(checkpoint_id=1, trigger=get_args(CheckpointTriggerKind)[0], turn=1, created_at=ts(18), duration_ms=10, size_bytes=1024, host=SnapshotDetails(snapshot_id="snap-h", size_bytes=1024, duration_ms=10, additional_files=1), sandboxes={"default": SnapshotDetails(snapshot_id="snap-s", size_bytes=2048, duration_ms=20, files=["/tmp/x.txt"])})).model_copy(update=base(18)),
    InterruptEvent(**base(19), source="limit", interrupted="tool_call", interrupted_tool_call_id="call_1"),
    ScoreEvent(**base(20), score=Score(value="C", answer="4", explanation="matched", metadata={"strict": True}), target="4", intermediate=False, scorer="match", scorer_args={"location": "any"}, model_usage={"gpt": ModelUsage(input_tokens=10, output_tokens=5, total_tokens=15)}),
    ScoreEvent(**base(21), score=Score(value=float("nan"), reason="no_response"), target=["4", "four"], intermediate=True),
    ScoreEditEvent(**base(22), score_name="match", edit=ScoreEdit(value="I", explanation="reviewer override", provenance=ProvenanceData(timestamp=ts(22), author="alice", reason="qa"))),
    SampleLimitEvent(**base(23), type="message", message="Message limit reached", limit=10),
    ErrorEvent(**base(24), error=EvalError(message="boom", traceback="Traceback...", traceback_ansi="Traceback...")),
    InfoEvent(**base(25), source="claude_code", data={"type": "system", "n": 1}),
    # data=None is not round-trippable by Python itself (exclude_none drops the required field), so use a plain value
    InfoEvent(**base(26), data="plain text"),
    StepEvent(**base(27), action="end", type="solver", name="generate"),
    SpanEndEvent(**base(28), id="span-solver"),
]

timeline = timeline_build(events, name="Default", description="agent view")
timeline.root.outline = Outline(nodes=[OutlineNode(event="ev-05", children=[OutlineNode(event="ev-07")])])

sample1 = EvalSample(
    id=1, epoch=1, input="What is 2+2?", target="4", sandbox=SandboxEnvironmentSpec("docker", "Dockerfile"), files=["hello.txt"], setup="echo hi",
    messages=[ChatMessageSystem(id="m1", content="Be helpful.", source="input"), ChatMessageUser(id="m2", content="What is 2+2?", source="input"), assistant,
              ChatMessageTool(id="m4", content="bash: not found", tool_call_id="call_1", function="bash", error=ToolCallError(type="unknown", message="bash: not found")), final],
    output=output,
    scores={"match": Score(value="C", answer="4", explanation="matched", metadata={"strict": True}), "num": Score(value=0.5), "flag": Score(value=True),
            "list": Score(value=[1, "a", False]), "dict": Score(value={"a": 1, "b": None, "c": "x"}), "unscored": Score(value=float("nan"), reason="no_response"),
            "listnan": Score(value=[1.0, float("nan")]), "dictnan": Score(value={"a": float("nan"), "b": float("inf"), "c": float("-inf")})},
    metadata={"check": "python3 -c 'print(4)'", "difficulty": 1, "tags": ["a", 2], "unicode": "héllo ✓ 日本"},
    store={"mini_swe_agent_exit_status": "Submitted", "steps": 3, "nested": {"k": None}},
    events=events,
    timelines=[timeline],
    model_usage={"gpt": ModelUsage(input_tokens=10, output_tokens=5, total_tokens=15, input_tokens_cache_read=2)},
    started_at=T0.isoformat(), completed_at=ts(2).isoformat(), total_time=2.0, working_time=1.5, uuid="uuid-1",
    attachments={"abc123": "attachment text"},
)
sample2 = EvalSample(
    id="s2", epoch=2, input=[ChatMessageSystem(id="m6", content="sys", source="input"), ChatMessageUser(id="m7", content=[ContentText(text="look"), ContentImage(image="data:image/png;base64,AAAA", detail="high")], source="input")],
    target=["a", "b"], choices=["a", "b"], messages=[ChatMessageUser(id="m8", content="look")],
    error=EvalError(message="sandbox exploded", traceback="trace", traceback_ansi="trace"),
    limit=EvalSampleLimit(type="message", limit=10, reason="Message limit reached"),
    uuid="uuid-2",
)

log = EvalLog(
    version=2, status="success",
    eval=EvalSpec(
        eval_id="eval-1", run_id="run1", created=T0.isoformat(), task="hello-swe", task_id="task1", task_version=0, task_file="tasks/hello.py",
        task_attribs={}, task_args={"difficulty": "easy"}, solver="generate", solver_args={}, tags=["needs_qa", "demo"],
        dataset=EvalDataset(name="hello", location="/tasks/hello/dataset.json", samples=2, sample_ids=[1, "s2"], shuffled=False),
        sandbox=SandboxEnvironmentSpec("docker", "Dockerfile"), model="gpt", model_generate_config=GenerateConfig(max_tokens=100), model_base_url="https://example.invalid", model_args={"k": "v"},
        config=EvalConfig(limit=2, epochs=2, max_samples=4, message_limit=10, token_limit=1000, time_limit=300, fail_on_error=False, sandbox_cleanup=True),
        revision=EvalRevision(type="git", origin="https://example.invalid/repo.git", commit="abc123", dirty=False),
        packages={"inspect_ai": "0.3.262"}, metadata={"owner": "kev"},
    ),
    plan=EvalPlan(name="plan", steps=[EvalPlanStep(solver="generate", params={"tool_calls": "loop"})], config=GenerateConfig(temperature=0.2)),
    results=EvalResults(total_samples=2, completed_samples=1, scores=[EvalScore(name="match", scorer="match", reducer="mean", scored_samples=1, unscored_samples=1, params={"location": "any"},
                        metrics={"accuracy": EvalMetric(name="accuracy", value=1.0), "stderr": EvalMetric(name="stderr", value=float("nan"), params={"cluster": None}), "inf": EvalMetric(name="inf", value=float("inf"))})]),
    stats=EvalStats(started_at=T0.isoformat(), completed_at=ts(5).isoformat(), model_usage={"gpt": ModelUsage(input_tokens=10, output_tokens=5, total_tokens=15)}),
    log_updates=[LogUpdate(edits=[TagsEdit(tags_add=["qa_passed"], tags_remove=["needs_qa"]), MetadataEdit(metadata_set={"reviewer": "alice"}, metadata_remove=["owner"])], provenance=ProvenanceData(timestamp=ts(30), author="alice", reason="QA complete"))],
    samples=[sample1, sample2],
)
log.reductions = [EvalSampleReductions(scorer="match", reducer="mean", samples=[EvalSampleScore(value="C", answer="4", sample_id=1), EvalSampleScore(value=float("nan"), sample_id="s2")])]
out = sys.argv[1]
write_eval_log(log, out)
print("tags", log.tags, "metadata", log.metadata)
