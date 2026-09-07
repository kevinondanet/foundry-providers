# Review source notes

Upstream commit: 3fea5104189022519d8e432950bc73f37103848e
Review date: September 7, 2026.
Audience: technical. Delivery: portable HTML in Codex desktop.
No live model tests or production changes.

## Local source fingerprints

- InspectAzureAI.Provider/AzureAIModelApi.cs: SHA-256 93f7f79ac51bebc94e42193c340d47080209bbc765999bcd8f86624460a08638
- InspectAzureAI.Provider/Anthropic/AnthropicFoundryModelApi.cs: SHA-256 997ffe789943bc79d55300b1351dbca33db2fc27a3b68b6ad9c325f8e6d860fa
- InspectAzureAI.Maf/MafConversion.cs: SHA-256 3a5fd09cbebcfe3f16bf0913b4559e55d8ed829430e04963fe72c1b855122053
- InspectAzureAI.Maf/InspectChatClient.cs: SHA-256 db679ccf50ca7a9816e1b851e3f70e20442cf00ae3d65883d006b33f81726c41

## Report structure
Technical summary; architecture and route evidence; scope/definitions; protocol, thinking, schema, bridge and implementation findings; methods and limitations; validation recommendations; open questions. Methodology is placed after results to keep an answer-first path.

## Visual contract
Two categorical lookup tables preserve exact routing and mapping distinctions. One bounded bar chart counts explicit mappings for three requested configuration fields, with a zero baseline and no inference of overall quality or endpoint support. Only three adapters are compared because those are the targeted routes, not a sampled population. No latency, cost, or quality measurements were fabricated.

## Mapping audit
Anthropic: completion_config in anthropic.py; OpenAI Responses: generation config in openai_responses.py; Azure inference: completion_params in azureai.py. Fields: response_schema, reasoning_effort, max_tokens. Rows: 3/3, 3/3, 1/3.

## Source handling
Upstream source links are pinned. Microsoft and Anthropic pages are current documentation retrieved on the review date; they can change. report-source.md is the supporting manuscript, artifact.json is the canonical delivered report input. All implementation observations are source-level only.

## Delivery QA
Canonical artifact validation, packaging and Chromium verification passed. Verified desktop width 1440 and narrow width 390, 22 blocks, 2 tables, 1 chart, source dialog interaction, and embedded payload equality. No live provider requests were run.
