# Vendored subset of Microsoft HVE Core

Source: https://github.com/microsoft/hve-core (plugin.json version 3.2.2, cloned 2026-09-08, `main`).
License: MIT, Copyright (c) Microsoft Corporation (see LICENSE alongside this file).
Files are copied unmodified from the upstream repository, keeping the upstream `.github/...` layout so that
relative `#file:` references and `references/`, `templates/` paths inside the artefacts resolve unchanged.
Only `plugin.json` is written here: it lists the subset instead of the full library and declares `rules` as a
directory (the Copilot CLI reads `rules` entries as directories; upstream lists individual files).

## Inventory (one line each)

Agents (`.github/agents`, selected with `--agent hve-core:<file-id>`; sub-agents appear in the `task` tool's `agent_type` enum)
- coding-standards/code-review.agent.md - orchestrator: bootstraps a change brief, dispatches perspective sub-agents, merges findings into review.md/metadata.json; has a hidden "workflow" autonomy mode for automation hosts.
- coding-standards/subagents/code-review-functional.agent.md - reads diff-state.json and the diff once, reviews logic/edge cases/error handling/contract, writes functional-findings.json (skill = null).
- coding-standards/subagents/code-review-standards.agent.md - same input, applies matching coding-standards skills (python-foundational) and writes standards-findings.json (skill = skill name).
- hve-core/rpi-agent.agent.md - Research, Plan, Implement, Review, Follow-up lifecycle coordinator; "Use automatic mode ..." in the prompt authorises unattended progression.
- hve-core/subagents/rpi-planner.agent.md, rpi-researcher.agent.md, rpi-review-builder.agent.md - bounded phase workers the RPI skills may dispatch.

Skills (`.github/skills/<package>/<name>/SKILL.md`; the model loads one with the `skill` tool, `{"skill": "<name>"}`)
- coding-standards/code-review - normative references for reviews: output-formats (findings JSON contract), severity-taxonomy, lens-checklists, depth-tiers, context-bootstrap, dispatch-loop, emission-modes, cross-skill-forks, change-risk-model, walkthrough-protocol.
- coding-standards/python-foundational - nine-section Python checklist (naming, idioms, typing, error handling, anti-patterns, maintainability) with a severity rubric; user-invocable: false, loaded by the standards lane or on request.
- hve-core/documentation - audit/drift/validate/author modes; templates/guide.md and templates/reference.md define document skeletons.
- rpi/rpi-quick, rpi-research, rpi-plan, rpi-plan-critique, rpi-implement, rpi-review - the RPI phase skills with their templates under .copilot-tracking/.

Commands (`.github/prompts`; exposed to the model as a skill named `<file>.prompt`, e.g. `git-commit-message.prompt`; the slash form is not expanded in `-p` mode)
- hve-core/git-commit-message.prompt.md - reads the staged diff and emits a conventional commit message per commit-message.instructions.md.

Instructions (`.github/instructions`; the CLI does not auto-apply plugin instructions - copy them into the workspace's `.github/instructions/` for `applyTo` matching)
- coding-standards/python-script.instructions.md (**/*.py), python-tests.instructions.md (**/*.py), bash/bash.instructions.md (**/*.sh), hve-core/markdown.instructions.md (**/*.md), hve-core/commit-message.instructions.md, hve-core/copilot-tracking.instructions.md, coding-standards/code-review/diff-computation + review-artifacts (imported by code-review.agent.md via #file:), shared/disclaimer-language.instructions.md (verbatim CAUTION block required at the end of review.md).
