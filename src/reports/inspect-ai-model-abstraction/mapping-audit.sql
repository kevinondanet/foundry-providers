WITH reviewed_mappings(adapter, response_schema, reasoning_effort, max_tokens) AS (
 VALUES ('Anthropic',1,1,1), ('OpenAI Responses',1,1,1), ('Azure inference',0,0,1)
)
SELECT adapter, response_schema + reasoning_effort + max_tokens AS mapped,
       3 AS checked, response_schema, reasoning_effort, max_tokens
FROM reviewed_mappings;
