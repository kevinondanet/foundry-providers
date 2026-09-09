SELECT
  json_extract(value, '$.outcome') AS "outcome",
  json_extract(value, '$.deployments') AS "deployments",
  json_extract(value, '$.total_deployments') AS "total_deployments",
  json_extract(value, '$.share') AS "share",
  json_extract(value, '$.models') AS "models",
  json_extract(value, '$.samples_per_eval') AS "samples_per_eval"
FROM json_each('[{"outcome": "Completed", "deployments": 16, "total_deployments": 21, "share": 0.7619047619047619, "models": "azureai/gpt-4o, azureai/gpt-5.4-mini, azureai/gpt-5.6-luna, azureai/gpt-5.6-terra, azureai/Kimi-K2.6, azureai/gpt-5.6-luna-2, azureai/gpt-5.6-sol, azureai/Kimi-K2.7-Code, azureai/grok-4.6, azureai/Cohere-command-a-plus-05-2026, azureai/Mistral-Large-3, azureai/MAI-Thinking-1, azureai/DeepSeek-V4-Flash, azureai/DeepSeek-V4-Pro, azureai/model-router, anthropic/claude-sonnet-4-6", "samples_per_eval": 3}, {"outcome": "Route error", "deployments": 3, "total_deployments": 21, "share": 0.14285714285714285, "models": "azureai/gpt-5.4-pro, azureai/Cohere-parse-v5, azureai/FLUX.2-pro", "samples_per_eval": 3}, {"outcome": "Timeout", "deployments": 1, "total_deployments": 21, "share": 0.047619047619047616, "models": "azureai/DeepSeek-V4-Flash-0731", "samples_per_eval": 3}, {"outcome": "Rate limit", "deployments": 1, "total_deployments": 21, "share": 0.047619047619047616, "models": "azureai/Ministral-3B", "samples_per_eval": 3}]');
