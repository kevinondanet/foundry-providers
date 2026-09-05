# hello-swe × claude-code

`myfoundry0406` (rg-mfa-foundry, eastus2) · 2026-09-04 17:08 UTC · sandbox docker

| deployment | format | route | status | accuracy | tokens | time | note |
|---|---|---|---|---:|---:|---:|---|
| claude-sonnet-4-6 | Anthropic | anthropic | incorrect | 0.000 | 72035 | 15.8s |  |
| Cohere-command-a-plus-05-2026 | Cohere | models | ok | 1.000 | 52581 | 63.3s |  |
| Cohere-parse-v5 | Cohere | models | skipped | - | - | - | chatCompletion=false |
| DeepSeek-V4-Flash | DeepSeek | models | incorrect | 0.000 | 18571 | 8.4s |  |
| DeepSeek-V4-Flash-0731 | DeepSeek | models | ok | 1.000 | 56378 | 63.0s |  |
| DeepSeek-V4-Pro | DeepSeek | models | ok | 1.000 | 56199 | 61.5s |  |
| FLUX.2-pro | Black Forest Labs | models | error | - | 0 | 193.5s | Error executing claude code agent 1: [claude-code:unrecognized_model] {… |
| gpt-4o | OpenAI | models | ok | 1.000 | 31863 | 10.5s |  |
| gpt-5.4-mini | OpenAI | models | ok | 1.000 | 32074 | 9.8s |  |
| gpt-5.4-pro | OpenAI | models | skipped | - | - | - | chatCompletion=false |
| gpt-5.6-luna | OpenAI | models | ok | 1.000 | 64618 | 17.5s |  |
| gpt-5.6-luna-2 | OpenAI | models | ok | 1.000 | 64572 | 17.0s |  |
| gpt-5.6-sol | OpenAI | models | ok | 1.000 | 48216 | 13.7s |  |
| gpt-5.6-terra | OpenAI | models | ok | 1.000 | 32078 | 12.3s |  |
| grok-4.6 | xAI | models | incorrect | 0.000 | 0 | 1200.8s | limit=time |
| Kimi-K2.6 | MoonshotAI | models | ok | 1.000 | 34902 | 13.8s |  |
| Kimi-K2.7-Code | MoonshotAI | models | ok | 1.000 | 52510 | 62.5s |  |
| MAI-Thinking-1 | Microsoft | models | error | - | 0 | 7.5s | Error executing claude code agent 1: [claude-code:unrecognized_model] {… |
| Ministral-3B | Mistral AI | models | error | - | 0 | 693.4s | Error executing claude code agent 1: [claude-code:unrecognized_model] {… |
| Mistral-Large-3 | Mistral AI | models | ok | 1.000 | 37295 | 10.5s |  |
| model-router | OpenAI | models | ok | 1.000 | 34201 | 107.5s |  |

19 run (13 ok, 0 partial, 3 incorrect, 0 unscored, 3 errored), 2 skipped; 688093 tokens.
