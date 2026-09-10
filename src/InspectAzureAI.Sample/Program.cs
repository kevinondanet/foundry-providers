using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using InspectAzureAI.Provider;
using System.Diagnostics;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Sample;

var arguments = args.ToList();
var fake = arguments.Remove("--fake");
var streamingArg = TakeOption(arguments, "--streaming");
var modelArg = TakeOption(arguments, "--model");
var routeArg = TakeOption(arguments, "--route");
var jsonFlag = arguments.Remove("--json");
var includeFailed = arguments.Remove("--include-failed");
var skipTools = arguments.Remove("--skip-tools");
var onlyArg = TakeOption(arguments, "--only");
var outArg = TakeOption(arguments, "--out");
var paramsArg = TakeOption(arguments, "--params");
var cacheExpiryArg = TakeOption(arguments, "--cache-expiry");
var costConfigArg = TakeOption(arguments, "--model-cost-config");
var parallel = 4;
if (TakeOption(arguments, "--parallel") is { } parallelArg)
{
    if (!int.TryParse(parallelArg, out parallel) || parallel <= 0)
    {
        Console.Error.WriteLine($"--parallel expects a positive number, got '{parallelArg}'\n\n{Cli.Help}");
        return 2;
    }
}
var maxTokensArg = TakeOption(arguments, "--max-tokens");
if (maxTokensArg is not null)
{
    if (maxTokensArg.Equals("none", StringComparison.OrdinalIgnoreCase))
    {
        Cli.MaxTokens = null;
    }
    else if (int.TryParse(maxTokensArg, out var maxTokens))
    {
        Cli.MaxTokens = maxTokens;
    }
    else
    {
        Console.Error.WriteLine($"--max-tokens expects a number or 'none', got '{maxTokensArg}'\n\n{Cli.Help}");
        return 2;
    }

    Cli.MaxTokensSet = true;
}

for (string? pair; (pair = TakeOption(arguments, "--model-arg")) is not null;)
{
    var eq = pair.IndexOf('=');
    if (eq <= 0)
    {
        Console.Error.WriteLine($"--model-arg expects key=value, got '{pair}'\n\n{Cli.Help}");
        return 2;
    }

    try
    {
        Cli.ExtraModelArgs[pair[..eq]] = ProviderUtil.ParseModelArgValue(pair[(eq + 1)..]);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"--model-arg {pair[..eq]}: {ex.Message}\n\n{Cli.Help}");
        return 2;
    }
}
var temperatureArg = TakeOption(arguments, "--temperature");
if (temperatureArg is not null)
{
    if (!double.TryParse(temperatureArg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature))
    {
        Console.Error.WriteLine($"--temperature expects a number, got '{temperatureArg}'\n\n{Cli.Help}");
        return 2;
    }

    Cli.Temperature = temperature;
}

var reasoningEffortArg = TakeOption(arguments, "--reasoning-effort");
if (reasoningEffortArg is not null)
{
    var effort = reasoningEffortArg.Trim().ToLowerInvariant();
    if (!ReasoningParams.EffortLevels.Contains(effort))
    {
        Console.Error.WriteLine($"--reasoning-effort expects one of {string.Join("|", ReasoningParams.EffortLevels)}, got '{reasoningEffortArg}'\n\n{Cli.Help}");
        return 2;
    }

    Cli.ReasoningEffort = effort;
}

var reasoningTokensArg = TakeOption(arguments, "--reasoning-tokens");
if (reasoningTokensArg is not null)
{
    if (!int.TryParse(reasoningTokensArg, out var reasoningTokens) || reasoningTokens <= 0)
    {
        Console.Error.WriteLine($"--reasoning-tokens expects a positive number, got '{reasoningTokensArg}'\n\n{Cli.Help}");
        return 2;
    }

    Cli.ReasoningTokens = reasoningTokens;
}

var reasoningSummaryArg = TakeOption(arguments, "--reasoning-summary");
if (reasoningSummaryArg is not null)
{
    var summary = reasoningSummaryArg.Trim().ToLowerInvariant();
    if (summary is not ("none" or "concise" or "detailed" or "auto"))
    {
        Console.Error.WriteLine($"--reasoning-summary expects one of none|concise|detailed|auto, got '{reasoningSummaryArg}'\n\n{Cli.Help}");
        return 2;
    }

    Cli.ReasoningSummary = summary;
}

if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
{
    Console.WriteLine(Cli.Help);
    return 0;
}

var command = arguments[0];
var rest = arguments.Skip(1).ToList();
try
{
    return command switch
    {
        "config" => Cli.Config(Cli.CreateApi(modelArg, streamingArg, fake)),
        "naming" => Cli.Naming(rest),
        "chat" => await Cli.Chat(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), string.Join(" ", rest)),
        "stream" => await Cli.Stream(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), string.Join(" ", rest)),
        "tools" => await Cli.ToolLoop(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), string.Join(" ", rest)),
        "image" => await Cli.Image(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), rest),
        "cache" => await Cli.CacheDemo(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), string.Join(" ", rest), cacheExpiryArg),
        "cost" => await Cli.CostDemo(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), string.Join(" ", rest), costConfigArg),
        "structured" => await Cli.Structured(Cli.CreateModelApi(routeArg, modelArg, streamingArg, fake), string.Join(" ", rest)),
        "retry-demo" => Cli.RetryDemo(Cli.CreateApi(modelArg, streamingArg, fake)),
        "token" => await Cli.Token(Cli.CreateApi(modelArg, streamingArg, fake)),
        "models" => await Cli.Models(Cli.CreateApi(modelArg, streamingArg, fake), fake, jsonFlag),
        "test-all" => await Cli.TestAll(Cli.CreateApi(modelArg, streamingArg, fake), fake, onlyArg, includeFailed, skipTools, jsonFlag),
        "capture" => await Cli.Capture(Cli.CreateApi(modelArg, streamingArg, fake), fake, onlyArg, includeFailed, outArg, paramsArg, parallel),
        "params" => await Cli.Params(Cli.CreateApi(modelArg, streamingArg, fake), fake, onlyArg, includeFailed, paramsArg ?? "all", parallel, jsonFlag, outArg),
        _ => Cli.Unknown(command),
    };
}
catch (UsageError ex)
{
    Console.Error.WriteLine($"{ex.Message}\n\n{Cli.Help}");
    return 2;
}
catch (PrerequisiteError ex)
{
    Console.Error.WriteLine(ProviderUtil.StripRichMarkup(ex.Message));
    return 2;
}
catch (Exception ex) when (Cli.IsSignInFailure(ex))
{
    Console.Error.WriteLine($"Entra ID sign-in failed: {Cli.SignInFailureMessage(ex)}\n\n{Cli.LoginHint}");
    return 3;
}
catch (RequestFailedException ex)
{
    Console.Error.WriteLine($"Azure request failed (HTTP {ex.Status}): {AzureAIModelApi.AzureErrorMessage(ex)}");
    return 3;
}
catch (ServiceResponseException ex)
{
    Console.Error.WriteLine($"Azure response could not be read (retryable): {ex.Message}");
    return 3;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Generation failed: {ex.Message}");
    return 3;
}

static string? TakeOption(List<string> arguments, string name)
{
    var index = arguments.IndexOf(name);
    if (index < 0 || index + 1 >= arguments.Count)
    {
        return null;
    }

    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}

namespace InspectAzureAI.Sample
{
    /// <summary>An invalid command-line option value (reported as a usage error, exit code 2).</summary>
    internal sealed class UsageError(string message) : Exception(message);

    /// <summary>Subcommand implementations for the sample console app.</summary>
    internal static partial class Cli
    {
        public const string Help = """
            InspectAzureAI.Sample - lite .NET port of the Inspect AI `azureai` model provider (az login only)

            usage: dotnet run --project src/InspectAzureAI.Sample -- <command> [options] [args]

            commands:
              config                    print the resolved endpoint, credential and model names (no call)
              naming <model...>         show service/canonical names, the Mistral rule and which token-limit
                                        parameter is sent for each model name (no call)
              chat [prompt]             one non-streaming completion
              stream [prompt]           one streaming completion (deltas printed as they arrive)
              tools [prompt]            native function-calling loop with the local get_weather tool: the model's
                                        tool_calls are executed and fed back until it answers in plain text
              image <path-or-url>       send an image (materialised as a data URI) with a question
              cache [prompt]            the same prompt twice under Inspect's prompt cache: the first call is stored
                                        (cache=write), the second served from disk with no provider call (cache=read)
                                        (--cache-expiry 1W|3D|12h, default 1W; INSPECT_CACHE_DIR picks the directory)
              cost [prompt]             what the model database knows about the model and one generation priced with
                                        --model-cost-config <file> ({"<model>": {"input", "output", "input_cache_write",
                                        "input_cache_read"} in $/million tokens); reports the call unpriced otherwise
              structured [prompt]       one generation constrained to a JSON schema (GenerateConfig.ResponseSchema, sent as
                                        response_format / output_format) and parsed back into a C# record
              retry-demo                show ShouldRetry / IsAuthFailure / HandleAzureError decisions
              token                     acquire an Entra ID token with the resolved credential and print
                                        who it belongs to (verifies that `az login` is picked up; no model call)
              models                    discover the Foundry resource behind AZUREAI_BASE_URL through Azure
                                        Resource Manager and list its model deployments (--json for machines)
              test-all                  smoke-test every healthy chat deployment: chat, stream, native tools; Anthropic
                                        deployments go through the Messages route and Responses-only OpenAI
                                        deployments through the Responses route automatically
                                        (--only a,b  --include-failed  --skip-tools  --json); exit 1 on any chat failure
              capture                   like test-all, but record every HTTP exchange (request line, headers with the
                                        bearer token redacted, body; response status, headers, body) as JSON for the
                                        educational dashboard in docs/dashboard (--out <file>  --only a,b  --include-failed
                                        --params all|a,b to add the parameter probes  --parallel n)
              params                    probe which request parameters each chat deployment accepts, one call per
                                        candidate on top of a baseline: temperature, top_p, seed/penalties, stop, n,
                                        logprobs, top_k, parallel_tool_calls, response_format, the other token-limit
                                        field, stream_options, and the family's reasoning controls; prints verdicts
                                        (accepted / ignored / rejected) with evidence and a matrix
                                        (--only a,b  --params a,b  --parallel n  --json  --out <file>)
              --help                    this text

            options:
              --model <name>            Inspect model specification (default: $INSPECT_AZUREAI_MODEL or gpt-5.4-mini)
                                        openai/<model> or anthropic/<model> for direct; add /azure/ for Foundry
              --streaming auto|true|false   the `streaming` model arg (default auto)
              --fake                    answer from a canned in-memory transport (no network, no sign-in)
              --temperature <n>         sampling temperature (default: not sent; gpt-5 deployments accept only 1)
              --max-tokens <n|none>     max_tokens sent (default: the provider's max_tokens(), 2048 for most models)
              --reasoning-effort <lvl>  Inspect's reasoning_effort (none|minimal|low|medium|high|xhigh|max), mapped to the
                                        family's field: reasoning_effort (OpenAI, grok, MAI), thinking {type} (DeepSeek,
                                        Kimi, Cohere), adaptive thinking + output_config.effort (Claude); none = off
              --reasoning-tokens <n>    Inspect's reasoning_tokens budget (Claude budget_tokens, Cohere token_budget)
              --reasoning-summary <s>   Inspect's reasoning_summary (none|concise|detailed|auto): reasoning.summary on the
                                        Responses route (opt-in; other routes ignore it)
              --model-arg key=value     repeatable; the Python -M model args: max_completion_tokens=true (MAI-Thinking-1),
                                        streaming, model_format=<vendor>, anthropic_beta=<list>. Direct providers require
                                        extra_body for additional wire fields; JSON values are parsed.
              --route models|anthropic|responses
                                        Foundry chat/stream/tools/image: the model-inference route, the Anthropic
                                        Messages route (/anthropic/v1/messages) for Claude deployments or the OpenAI
                                        Responses route (/openai/v1/responses) for gpt-5.6* / o-series / -pro / codex
                                        deployments

            environment:
              OPENAI_API_KEY / ANTHROPIC_API_KEY                    direct service credentials
              OPENAI_BASE_URL / ANTHROPIC_BASE_URL                  direct endpoints; required when Azure settings exist
              AZURE_ENDPOINT_URL / AZUREAI_ENDPOINT_URL / AZUREAI_BASE_URL   endpoint (in that order)
              INSPECT_EVAL_MODEL_BASE_URL                           last-resort endpoint fallback
              AZUREAI_AUDIENCE                                      Entra ID token scope (default https://cognitiveservices.azure.com/.default)
              AZURE_TENANT_ID / AZURE_CLIENT_ID                     Azure.Identity: tenant pin / user-assigned managed identity
              AZUREAI_RESOURCE_ID / AZURE_SUBSCRIPTION_ID           models/test-all: skip or narrow the ARM search
              AZUREAI_ANTHROPIC_BASE_URL / AZURE_ANTHROPIC_BASE_URL  Anthropic route base URL; derived from AZUREAI_BASE_URL when unset
              AZUREAI_OPENAI_BASE_URL / AZURE_OPENAI_BASE_URL        Responses route base URL; derived from AZUREAI_BASE_URL when unset

            Authentication is Entra ID only (DefaultAzureCredential): sign in with `az login` (and `az account set`),
            then run `token` to confirm which identity the credential resolves to. No API key variables are read.
            """;

        /// <summary>What to try when token acquisition fails.</summary>
        public const string LoginHint = """
            Hints:
              az login                                   sign in (add --tenant <id> for a specific tenant)
              az account set --subscription <name|id>    pick the subscription that owns the endpoint
              AZURE_TENANT_ID=<id>                       pin the tenant DefaultAzureCredential signs in to
              AZUREAI_AUDIENCE=<scope>                   change the token scope (default https://cognitiveservices.azure.com/.default)
            """;

        public static bool IsSignInFailure(Exception ex) =>
            ex is CredentialUnavailableException or AuthenticationFailedException
            || (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsSignInFailure));

        public static string SignInFailureMessage(Exception ex) =>
            ex is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                ? SignInFailureMessage(aggregate.InnerExceptions[^1])
                : ex.Message;

        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly ToolInfo WeatherTool = new("get_weather", "Get the current weather for a city.")
        {
            Parameters = new ToolParams
            {
                Properties = new Dictionary<string, ToolParam>
                {
                    ["city"] = new() { Type = ["string"], Description = "City name", MinLength = 1 },
                },
                Required = ["city"],
            },
        };

        public static AzureAIModelApi CreateApi(string? model, string? streaming, bool fake)
        {
            model ??= Environment.GetEnvironmentVariable("INSPECT_AZUREAI_MODEL") ?? "gpt-5.4-mini";
            try
            {
                if (fake)
                {
                    // Offline: a canned transport and a dummy token, so no sign-in is attempted.
                    return new AzureAIModelApi(model, "https://fake.local/models", streaming: streaming, modelArgs: ExtraModelArgs,
                        settings: new AzureAIClientSettings { Transport = FakeAzure.Transport(), TokenCredential = FakeAzure.Credential });
                }

                return new AzureAIModelApi(model, streaming: streaming, modelArgs: ExtraModelArgs);
            }
            catch (ArgumentException ex)
            {
                // NormalizeStreamArg rejects anything but auto/true/false with the Python message.
                throw new UsageError($"--streaming: {ex.Message}");
            }
        }

        /// <summary>Creates the provider for the selected route: model-inference (default), the Anthropic Messages route or the OpenAI Responses route.</summary>
        public static IModelApi CreateModelApi(string? route, string? model, string? streaming, bool fake)
        {
            if (!fake) return InspectAzureAI.Eval.Model.Models.CreateApi(model, route: route, streaming: streaming, modelArgs: ExtraModelArgs);
            if (route is null || route.Equals("models", StringComparison.OrdinalIgnoreCase))
            {
                return CreateApi(model, streaming, fake);
            }

            var anthropic = route.Equals("anthropic", StringComparison.OrdinalIgnoreCase);
            if (!anthropic && !route.Equals("responses", StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageError($"--route expects models, anthropic or responses, got '{route}'");
            }

            if (fake)
            {
                throw new UsageError($"--route {route.ToLowerInvariant()} has no --fake endpoint");
            }

            try
            {
                model ??= Environment.GetEnvironmentVariable("INSPECT_AZUREAI_MODEL");
                return anthropic
                    ? new AnthropicFoundryModelApi(model ?? "claude-sonnet-4-6", streaming: streaming, modelArgs: ExtraModelArgs)
                    : new OpenAIResponsesModelApi(model ?? "gpt-5.6-sol", streaming: streaming, modelArgs: ExtraModelArgs);
            }
            catch (ArgumentException ex)
            {
                throw new UsageError($"--streaming: {ex.Message}");
            }
        }

        public static int Config(AzureAIModelApi api)
        {
            Console.WriteLine($"endpoint            : {api.EndpointUrl}");
            Console.WriteLine("auth mode           : Entra ID (Authorization: Bearer only; no API key variables are read)");
            Console.WriteLine($"credential          : {AzureHosting.Describe(api.Credential)}");
            Console.WriteLine("                      run `token` to confirm the sign-in (az login) yields a token");
            Console.WriteLine($"model_name          : {api.ModelName}");
            Console.WriteLine($"service_model_name  : {api.ServiceModelName()}");
            Console.WriteLine($"canonical_name      : {api.CanonicalName()}");
            Console.WriteLine($"org_prefix          : {api.OrgPrefix ?? "(none)"}");
            Console.WriteLine($"is_mistral          : {api.IsMistral()}");
            Console.WriteLine($"max_tokens()        : {(api.MaxTokens()?.ToString() ?? "null (server default)")}");
            Console.WriteLine($"streaming           : {(api.Streaming?.ToString() ?? "auto")}");
            Console.WriteLine($"token limit param   : {TokenLimitParam(api)}");
            Console.WriteLine($"family              : {api.FamilyHint} — {ReasoningParams.Describe(api.FamilyHint).Notes}");
            var reasoning = api.ReasoningRequestParams(DefaultConfig(api));
            Console.WriteLine($"reasoning params    : {(reasoning.Count == 0 ? "(none; pass --reasoning-effort or --reasoning-tokens)" : reasoning.ToJsonString())}");
            Console.WriteLine($"connection_key      : {api.ConnectionKey()}");
            Console.WriteLine($"model_extras        : {JsonSerializer.Serialize(api.ModelArgs)}");
            return 0;
        }

        public static int Naming(List<string> models)
        {
            if (models.Count == 0)
            {
                models =
                [
                    "gpt-5.4-mini", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-4o", "model-router", "DeepSeek-V4-Flash", "DeepSeek-V4-Flash-0731",
                    "Mistral-Large-3", "Ministral-3B", "MAI-Thinking-1", "Kimi-K2.7-Code", "Kimi-K2.6", "Cohere-command-a-plus-05-2026",
                    "grok-4.6", "claude-sonnet-4-6", "moonshotai/kimi-k2.5", "my-custom-org/gpt-4o",
                ];
            }

            Console.WriteLine($"{"model_name",-30} {"family",-11} {"mistral",-8} {"max_tokens",-11} {"token limit param",-48} reasoning control");
            foreach (var model in models)
            {
                var api = new AzureAIModelApi(model, "https://example.com/models", modelArgs: ExtraModelArgs,
                    settings: new AzureAIClientSettings { TokenCredential = FakeAzure.Credential });
                Console.WriteLine($"{model,-30} {api.FamilyHint,-11} {api.IsMistral(),-8} {api.MaxTokens()?.ToString() ?? "null",-11} {TokenLimitParam(api),-48} {ReasoningControl(api.FamilyHint)}");
            }

            Console.WriteLine();
            Console.WriteLine("claude-* deployments are served on the Anthropic Messages route (--route anthropic), not the model-inference route.");
            Console.WriteLine("gpt-5.6* / o-series / -pro / codex deployments prefer the OpenAI Responses route (--route responses); the model-inference route rejects some of their requests.");
            return 0;
        }

        /// <summary>The family's reasoning control in one line (see <see cref="ReasoningParams.Describe"/>).</summary>
        private static string ReasoningControl(ModelFamilyHint family)
        {
            var support = ReasoningParams.Describe(family);
            return support.Toggle switch
            {
                ThinkingToggle.EnabledDisabled => "thinking {type: enabled|disabled}" + (support.BudgetKey is null ? "" : $" + {support.BudgetKey}"),
                ThinkingToggle.Adaptive => "thinking {type: adaptive} + output_config.effort",
                _ => support.EffortKey ?? "(none)",
            };
        }

        /// <summary>Which body field carries the token limit for this model (README fidelity notes 2 and 18).</summary>
        private static string TokenLimitParam(AzureAIModelApi api) =>
            api.MaxTokens() is null && !MaxTokensSet ? "(not sent: max_tokens() is null)"
            : api.ForceMaxCompletionTokens ? "max_completion_tokens (forced by -M max_completion_tokens=true)"
            : OpenAIUtil.NeedsMaxCompletionTokens(api.ModelFamily()) ? "max_completion_tokens (gpt-5 / o-series rule)"
            : api.SendsMaxCompletionTokens ? "max_completion_tokens (Microsoft family rule)"
            : "max_tokens";

        public static async Task<int> Chat(IModelApi api, string prompt)
        {
            var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt)) };
            var result = await api.GenerateAsync(input, [], ToolChoice.Auto, DefaultConfig(api));
            return Report(result);
        }

        public static async Task<int> Stream(IModelApi api, string prompt)
        {
            var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt)) };
            Console.WriteLine("-- stream events --");
            var result = await api.GenerateAsync(input, [], ToolChoice.Auto, DefaultConfig(api), OnStream);
            Console.WriteLine();
            return Report(result);
        }

        public static async Task<int> ToolLoop(IModelApi api, string prompt)
        {
            var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt, "What is the weather like in Paris right now? Use the get_weather tool.")) };
            var wire = api switch
            {
                AzureAIModelApi => "native tool_calls, model-inference route",
                OpenAIResponsesModelApi => "function_call items, OpenAI Responses route",
                _ => "tool_use blocks, Anthropic Messages route",
            };
            for (var turn = 0; turn < 5; turn++)
            {
                Console.WriteLine($"== turn {turn + 1} ({wire}) ==");
                var result = await api.GenerateAsync(input, [WeatherTool], ToolChoice.Auto, DefaultConfig(api));
                Report(result);
                var output = result.OutputOrThrow();
                input.Add(output.Message);
                if (output.Message.ToolCalls is not { Count: > 0 } calls)
                {
                    Console.WriteLine($"final answer: {output.Completion}");
                    return 0;
                }

                foreach (var call in calls)
                {
                    input.Add(ExecuteTool(call));
                }
            }

            Console.WriteLine("tool loop did not converge in 5 turns");
            return 1;
        }

        public static async Task<int> Image(IModelApi api, List<string> rest)
        {
            if (rest.Count == 0)
            {
                Console.Error.WriteLine("usage: image <path-or-url> [question]");
                return 2;
            }

            var dataUri = await MaterializeImage(rest[0]);
            var question = rest.Count > 1 ? string.Join(" ", rest.Skip(1)) : "Describe this image in one sentence.";
            var input = new List<ChatMessage>
            {
                new ChatMessageUser(new Content[] { new ContentText(question), new ContentImage(dataUri) }),
            };
            var result = await api.GenerateAsync(input, [], ToolChoice.Auto, DefaultConfig(api));
            return Report(result);
        }

        /// <summary>Acquires a token with the resolved credential and prints who it belongs to (never the token).</summary>
        public static async Task<int> Token(AzureAIModelApi api)
        {
            Console.WriteLine($"credential : {AzureHosting.Describe(api.Credential.Inner)}");
            Console.WriteLine($"scope      : {api.Credential.Scope}");
            try
            {
                var token = await api.Credential.GetTokenAsync(new TokenRequestContext([api.Credential.Scope]), CancellationToken.None);
                Console.WriteLine($"acquired   : yes, expires {token.ExpiresOn:u}");
                var info = EntraTokenInfo.TryParse(token.Token);
                if (info is null)
                {
                    Console.WriteLine("claims     : (opaque token, not a JWT)");
                    return 0;
                }

                Console.WriteLine($"identity   : {info.UserPrincipalName ?? info.AppId ?? "(unknown)"}{(info.Name is null ? "" : $" ({info.Name})")}");
                Console.WriteLine($"tenant     : {info.TenantId ?? "(unknown)"}");
                Console.WriteLine($"object id  : {info.ObjectId ?? "(unknown)"}");
                Console.WriteLine($"audience   : {info.Audience ?? "(unknown)"}");
                if (info.Scopes.Count > 0)
                {
                    Console.WriteLine($"scopes     : {string.Join(' ', info.Scopes)}");
                }

                return 0;
            }
            catch (Exception ex) when (IsSignInFailure(ex))
            {
                Console.Error.WriteLine($"Could not acquire a token: {SignInFailureMessage(ex)}\n\n{LoginHint}");
                return 3;
            }
        }

        public static int RetryDemo(AzureAIModelApi api)
        {
            var cases = new (string Label, Exception Error)[]
            {
                ("HTTP 429 with Retry-After: 7", new RequestFailedException(CannedResponse.Error(429, "Too many requests", new Dictionary<string, string> { ["Retry-After"] = "7" }))),
                ("HTTP 429 with x-ratelimit-reset-tokens: 1m30s", new RequestFailedException(CannedResponse.Error(429, "Too many requests", new Dictionary<string, string> { ["x-ratelimit-reset-tokens"] = "1m30s" }))),
                ("HTTP 503", new RequestFailedException(CannedResponse.Error(503, "Service unavailable"))),
                ("HTTP 408", new RequestFailedException(CannedResponse.Error(408, "Request timeout"))),
                ("HTTP 400", new RequestFailedException(CannedResponse.Error(400, "Bad request"))),
                ("HTTP 401", new RequestFailedException(CannedResponse.Error(401, "Unauthorized"))),
                ("HTTP 404", new RequestFailedException(CannedResponse.Error(404, "Not found"))),
                ("transport failure (status 0, ~ServiceRequestError)", new RequestFailedException("connection refused")),
                ("response read failure (~ServiceResponseError)", new ServiceResponseException("read timeout")),
                ("network timeout, normalised by AsAzureError", AzureAIModelApi.AsAzureError(new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout of 0:01:40."))!),
                ("SDK retries exhausted, normalised (last: IOException)", AzureAIModelApi.AsAzureError(new AggregateException("Retry failed after 3 tries.", new RequestFailedException("connection refused"), new IOException("socket reset")))!),
                ("non-Azure exception", new InvalidOperationException("Streaming response ended without delivering any chunks.")),
            };

            Console.WriteLine($"{"case",-52} {"should_retry",-40} is_auth_failure");
            foreach (var (label, error) in cases)
            {
                Console.WriteLine($"{label,-52} {api.ShouldRetry(error),-40} {api.IsAuthFailure(error)}");
            }

            Console.WriteLine();
            Console.WriteLine("handle_azure_error:");
            foreach (var (label, error) in new[]
                     {
                         ("HTTP 400 mentioning 'maximum context length'", new RequestFailedException(CannedResponse.Error(400, "This model's maximum context length is 4096 tokens."))),
                         ("HTTP 400 other", new RequestFailedException(CannedResponse.Error(400, "Invalid request"))),
                         ("HTTP 500", new RequestFailedException(CannedResponse.Error(500, "boom"))),
                     })
            {
                var call = ModelCall.Create(new JsonObject());
                try
                {
                    var outcome = api.HandleAzureError(error, call);
                    Console.WriteLine(outcome.Output is not null
                        ? $"  {label,-48} -> ModelOutput stop_reason={outcome.Output.StopReason.ToWire()} content={outcome.Output.Completion}"
                        : $"  {label,-48} -> returned {outcome.Error!.GetType().Name} (terminal, no retry)");
                }
                catch (RequestFailedException ex)
                {
                    Console.WriteLine($"  {label,-48} -> re-raised HTTP {ex.Status} for retry classification ({api.ShouldRetry(ex)})");
                }
            }

            return 0;
        }

        /// <summary>A question that benefits from thinking (10403 = 101 × 103) without needing a long answer.</summary>
        public const string ReasoningPrompt = ProbeCatalog.PromptReasoning;

        private static readonly (string Check, string Prompt, Func<GenerateConfig, GenerateConfig>? Configure)[] SmokeChecks =
        [
            ("chat", "Reply with exactly: ok", null),
            ("stream", "Count from 1 to 3 on one line.", null),
            ("tools", "What is the weather in Oslo right now? Use the get_weather tool.", null),
            ("reasoning", ReasoningPrompt, c => c with { ReasoningEffort = ReasoningEffort ?? "medium", ReasoningTokens = ReasoningTokens, ReasoningSummary = ReasoningSummary }),
        ];

        private static readonly string[] ReasoningRequestKeys = ["reasoning_effort", "thinking", "output_config", "reasoning"];

        /// <summary>
        /// What the reasoning check saw: <c>text</c> (reasoning text came back), <c>hidden</c> (only a reasoning token
        /// count), <c>none</c> (a reasoning field went out, nothing came back), <c>n/a</c> (no reasoning field on the wire).
        /// Reads the recorded request body, because the ModelCall snapshot omits model extras.
        /// </summary>
        private static string ReasoningVisibility(ModelOutput output, IReadOnlyList<HttpExchange> exchanges)
        {
            if (output.Message.ContentList.OfType<ContentReasoning>().Any(r => r.Reasoning.Length > 0))
            {
                return "text";
            }

            if (output.Usage?.ReasoningTokens is > 0)
            {
                return "hidden";
            }

            return ReasoningFieldSent(exchanges) ? "none" : "n/a";
        }

        private static bool ReasoningFieldSent(IReadOnlyList<HttpExchange> exchanges)
        {
            if (exchanges.LastOrDefault()?.RequestBody is not { } body)
            {
                return false;
            }

            try
            {
                return JsonNode.Parse(body) is JsonObject request && ReasoningRequestKeys.Any(request.ContainsKey);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>A provider whose HTTP exchanges are recorded (headers, bodies), for verdicts that need the wire and for the dashboard.</summary>
        private sealed class CapturedTarget(IModelApi api, IReadOnlyList<HttpExchange> exchanges, IDisposable? handler) : IDisposable
        {
            public IModelApi Api { get; } = api;

            public IReadOnlyList<HttpExchange> Exchanges { get; } = exchanges;

            public void Dispose()
            {
                (Api as IDisposable)?.Dispose();
                handler?.Dispose();
            }
        }

        private static CapturedTarget CreateCapturedTarget(AzureAIModelApi api, AzureAIClientSettings shared, FoundryDeployment deployment, string route, IReadOnlyDictionary<string, object?> args)
        {
            if (route == "anthropic")
            {
                var handler = new HttpCaptureHandler();
                var claude = new AnthropicFoundryModelApi(deployment.Name, AnthropicFoundryModelApi.DeriveBaseUrl(api.EndpointUrl), streaming: api.Streaming, modelArgs: args, settings: shared, handler: handler);
                return new CapturedTarget(claude, handler.Exchanges, handler);
            }

            if (route == "responses")
            {
                var handler = new HttpCaptureHandler();
                var responses = new OpenAIResponsesModelApi(deployment.Name, OpenAIResponsesModelApi.DeriveBaseUrl(api.EndpointUrl), streaming: api.Streaming, modelArgs: args, settings: shared, handler: handler);
                return new CapturedTarget(responses, handler.Exchanges, handler);
            }

            var capture = new HttpCapturePolicy();
            var settings = shared with
            {
                ConfigureClientOptions = options =>
                {
                    shared.ConfigureClientOptions?.Invoke(options);
                    options.AddPolicy(capture, HttpPipelinePosition.PerRetry);   // after the bearer-token policy, before the transport
                },
            };
            var modelArgs = new Dictionary<string, object?>(args) { ["model_format"] = deployment.Format };   // ARM's vendor string picks the reasoning mapping
            return new CapturedTarget(new AzureAIModelApi(deployment.Name, api.EndpointUrl, streaming: api.Streaming, modelArgs: modelArgs, settings: settings), capture.Exchanges, null);
        }

        private static TokenCredential ArmCredential(AzureAIModelApi api) => api.Credential.Inner;

        /// <summary>Discovers the resource behind the endpoint and prints its deployments.</summary>
        public static async Task<int> Models(AzureAIModelApi api, bool fake, bool json)
        {
            if (fake)
            {
                Console.Error.WriteLine("models needs Azure Resource Manager; it is not available with --fake.");
                return 2;
            }

            using var catalog = new FoundryCatalog(ArmCredential(api));
            var (resource, deployments) = await catalog.DiscoverAsync(api.EndpointUrl);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { resource, deployments }, Pretty));
                return 0;
            }

            PrintResource(resource, api.EndpointUrl);
            Console.WriteLine();
            Console.WriteLine($"{"deployment",-30} {"model",-30} {"format",-11} {"version",-11} {"sku",-15} {"capacity",8}  {"state",-10} chat");
            foreach (var d in deployments)
            {
                Console.WriteLine($"{d.Name,-30} {d.Model,-30} {d.Format,-11} {d.Version ?? "-",-11} {d.Sku ?? "-",-15} {d.Capacity?.ToString() ?? "-",8}  {d.State,-10} {(d.SupportsChat ? "yes" : "no")}");
            }

            Console.WriteLine();
            Console.WriteLine($"{deployments.Count(d => d.IsSucceeded && d.SupportsChat)} of {deployments.Count} deployments are healthy chat deployments; run test-all to exercise them.");
            return 0;
        }

        /// <summary>Runs chat, stream and native-tool smoke tests against every healthy chat deployment.</summary>
        public static async Task<int> TestAll(AzureAIModelApi api, bool fake, string? only, bool includeFailed, bool skipTools, bool json)
        {
            if (fake)
            {
                Console.Error.WriteLine("test-all needs Azure Resource Manager; it is not available with --fake.");
                return 2;
            }

            using var catalog = new FoundryCatalog(ArmCredential(api));
            var (resource, deployments) = await catalog.DiscoverAsync(api.EndpointUrl);
            var wanted = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shared = api.Settings with { TokenCredential = api.Credential.Inner };   // one credential, one token cache
            var rows = new List<SmokeRow>();

            if (!json)
            {
                PrintResource(resource, api.EndpointUrl);
                Console.WriteLine();
            }

            foreach (var deployment in deployments)
            {
                if (wanted is not null && !wanted.Contains(deployment.Name))
                {
                    continue;
                }

                var row = new SmokeRow(deployment);
                rows.Add(row);
                var route = RouteFor(deployment);
                if (!includeFailed && (!deployment.IsSucceeded || (!deployment.SupportsChat && route != "responses")))
                {
                    row.Skipped = !deployment.IsSucceeded ? $"provisioningState={deployment.State}" : "chatCompletion=false";
                    if (!json) Console.WriteLine($"{deployment.Name,-22} skipped ({row.Skipped})");
                    continue;
                }

                var args = new Dictionary<string, object?>(ExtraModelArgs);
                var target = CreateCapturedTarget(api, shared, deployment, route, args);
                if (RouteNote(route) is { } note)
                {
                    row.Notes.Add(note);
                }

                try
                {
                    var forcedMaxCompletionTokens = false;
                    foreach (var (check, prompt, configure) in SmokeChecks)
                    {
                        if (check == "tools" && skipTools)
                        {
                            row.Results[check] = "skipped";
                            continue;
                        }

                        var status = await SmokeAsync(target, check, prompt, configure, row);
                        if (status == "fail" && route == "models" && !forcedMaxCompletionTokens
                            && row.Errors.GetValueOrDefault(check, "").Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
                        {
                            // Reasoning models (e.g. MAI-Thinking-1) reject max_tokens; Python's name rule does not know them.
                            args["max_completion_tokens"] = true;
                            target.Dispose();
                            target = CreateCapturedTarget(api, shared, deployment, route, args);
                            forcedMaxCompletionTokens = true;
                            row.Notes.Add("needs --model-arg max_completion_tokens=true");
                            row.Errors.Remove(check);
                            status = await SmokeAsync(target, check, prompt, configure, row);
                        }

                        row.Results[check] = status;
                    }
                }
                finally
                {
                    target.Dispose();
                }

                if (!json)
                {
                    Console.WriteLine($"{deployment.Name,-22} chat={row.Results.GetValueOrDefault("chat")} stream={row.Results.GetValueOrDefault("stream")} tools={row.Results.GetValueOrDefault("tools")} reasoning={row.Results.GetValueOrDefault("reasoning")}{(row.ReasoningTokens is { } rt ? $"({rt} tok)" : "")} tokens={row.Tokens} ms={row.Milliseconds}{(row.Notes.Count > 0 ? "  " + string.Join("; ", row.Notes) : "")}");
                }
            }

            var tested = rows.Where(r => r.Skipped is null).ToList();
            var failedChat = tested.Where(r => r.Results.GetValueOrDefault("chat") != "ok").ToList();
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    resource, endpoint = api.EndpointUrl,
                    results = rows.Select(r => new { deployment = r.Deployment.Name, format = r.Deployment.Format, state = r.Deployment.State, skipped = r.Skipped, checks = r.Results, reasoningTokens = r.ReasoningTokens, tokens = r.Tokens, ms = r.Milliseconds, notes = r.Notes, errors = r.Errors }),
                    ok = failedChat.Count == 0,
                }, Pretty));
                return failedChat.Count == 0 ? 0 : 1;
            }

            Console.WriteLine();
            Console.WriteLine($"{"deployment",-30} {"format",-11} {"state",-10} {"chat",-8} {"stream",-8} {"tools",-8} {"reasoning",-16} {"tokens",7} {"ms",7}  note");
            foreach (var r in rows)
            {
                Console.WriteLine(r.Skipped is not null
                    ? $"{r.Deployment.Name,-30} {r.Deployment.Format,-11} {r.Deployment.State,-10} skipped: {r.Skipped}"
                    : $"{r.Deployment.Name,-30} {r.Deployment.Format,-11} {r.Deployment.State,-10} {Cell(r.Results, "chat"),-8} {Cell(r.Results, "stream"),-8} {Cell(r.Results, "tools"),-8} {ReasoningCell(r),-16} {r.Tokens,7} {r.Milliseconds,7}  {string.Join("; ", r.Notes)}");
            }

            foreach (var r in rows.Where(r => r.Errors.Count > 0))
            {
                foreach (var (check, error) in r.Errors)
                {
                    Console.WriteLine($"  {r.Deployment.Name}/{check}: {error}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{tested.Count - failedChat.Count}/{tested.Count} tested deployments answered chat; {rows.Count - tested.Count} skipped.");
            return failedChat.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// Records one complete HTTP exchange per check (chat, stream, tools) for every deployment: request line,
        /// headers (bearer token redacted) and body; response status, headers and body (raw SSE for streams); plus
        /// the parsed output. The JSON feeds the educational dashboard in <c>docs/dashboard</c>.
        /// </summary>
        public static async Task<int> Capture(AzureAIModelApi api, bool fake, string? only, bool includeFailed, string? outPath, string? paramsFilter, int parallel)
        {
            if (fake)
            {
                Console.Error.WriteLine("capture needs Azure Resource Manager and live endpoints; it is not available with --fake.");
                return 2;
            }

            using var catalog = new FoundryCatalog(ArmCredential(api));
            var (resource, deployments) = await catalog.DiscoverAsync(api.EndpointUrl);
            var wanted = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shared = api.Settings with { TokenCredential = api.Credential.Inner };   // one credential, one token cache
            var entries = new JsonArray();
            var report = new JsonObject
            {
                ["capturedAt"] = DateTimeOffset.UtcNow.ToString("u"),
                ["resource"] = new JsonObject { ["name"] = resource.Name, ["kind"] = resource.Kind, ["location"] = resource.Location, ["resourceGroup"] = resource.ResourceGroup },
                ["endpoint"] = api.EndpointUrl,
                ["anthropicEndpoint"] = AnthropicFoundryModelApi.DeriveBaseUrl(api.EndpointUrl) + "/v1/messages",
                ["responsesEndpoint"] = OpenAIResponsesModelApi.DeriveBaseUrl(api.EndpointUrl) + "/responses",
                ["credential"] = AzureHosting.Describe(api.Credential),
                ["deployments"] = entries,
            };

            foreach (var deployment in deployments)
            {
                if (wanted is not null && !wanted.Contains(deployment.Name))
                {
                    continue;
                }

                var route = RouteFor(deployment);
                var checks = new JsonArray();
                var entry = new JsonObject
                {
                    ["name"] = deployment.Name,
                    ["model"] = deployment.Model,
                    ["format"] = deployment.Format,
                    ["version"] = deployment.Version,
                    ["sku"] = deployment.Sku,
                    ["capacity"] = deployment.Capacity,
                    ["state"] = deployment.State,
                    ["chatCapable"] = deployment.SupportsChat,
                    ["route"] = route,
                    ["capabilities"] = new JsonObject(deployment.Capabilities.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                    ["checks"] = checks,
                };
                entries.Add(entry);
                if (!includeFailed && (!deployment.IsSucceeded || (!deployment.SupportsChat && route != "responses")))
                {
                    entry["skipped"] = !deployment.IsSucceeded ? $"provisioningState={deployment.State}" : "chatCompletion=false";
                    Console.Error.WriteLine($"{deployment.Name,-30} skipped ({entry["skipped"]})");
                    continue;
                }

                var args = new Dictionary<string, object?>(ExtraModelArgs);
                var summary = new List<string>();
                foreach (var (check, prompt, configure) in SmokeChecks)
                {
                    var result = await CaptureCheckAsync(api, shared, deployment, route, check, prompt, configure, args);
                    checks.Add(result);
                    var error = result["error"]?.ToString() ?? "";
                    if (route == "models" && !args.ContainsKey("max_completion_tokens") && error.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
                    {
                        // Reasoning models (e.g. MAI-Thinking-1) reject max_tokens; keep the rejection on record, then retry.
                        args["max_completion_tokens"] = true;
                        result["note"] = "rejected max_tokens; retried with -M max_completion_tokens=true (next entry)";
                        result = await CaptureCheckAsync(api, shared, deployment, route, check, prompt, configure, args);
                        result["note"] = "retry with max_completion_tokens=true";
                        checks.Add(result);
                    }

                    summary.Add($"{check}={(result["ok"]?.GetValue<bool>() == true ? "ok" : "fail")}");
                }

                var forced = args.ContainsKey("max_completion_tokens") && !ExtraModelArgs.ContainsKey("max_completion_tokens");
                Console.Error.WriteLine($"{deployment.Name,-30} {string.Join(' ', summary)}{(forced ? "  (max_completion_tokens=true)" : "")}");
            }

            if (paramsFilter is not null)
            {
                // The parameter probes, in parallel across the deployments that were tested above.
                var probed = deployments.Where(d => entries.OfType<JsonObject>().Any(e => e["name"]?.ToString() == d.Name && e["skipped"] is null)).ToList();
                var filter = ParseParamsFilter(paramsFilter);
                Console.Error.WriteLine($"probing parameters on {probed.Count} deployment(s), {parallel} at a time");
                foreach (var (deployment, parameters) in await ProbeAllAsync(api, shared, probed, filter, parallel, Console.Error.WriteLine))
                {
                    entries.OfType<JsonObject>().First(e => e["name"]?.ToString() == deployment.Name)["params"] = parameters;
                }
            }

            var json = report.ToJsonString(Pretty);
            if (outPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                await File.WriteAllTextAsync(outPath, json);
                Console.Error.WriteLine($"wrote {outPath} ({json.Length:N0} chars)");
            }

            return 0;
        }

        private static async Task<JsonObject> CaptureCheckAsync(
            AzureAIModelApi api, AzureAIClientSettings shared, FoundryDeployment deployment, string route, string check, string prompt,
            Func<GenerateConfig, GenerateConfig>? configure, Dictionary<string, object?> args)
        {
            using var target = CreateCapturedTarget(api, shared, deployment, route, args);
            var result = new JsonObject { ["check"] = check, ["prompt"] = prompt, ["ok"] = false };
            var watch = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                var input = new List<ChatMessage> { new ChatMessageUser(prompt) };
                var config = configure?.Invoke(DefaultConfig(target.Api)) ?? DefaultConfig(target.Api);
                GenerateResult generated = check switch
                {
                    "stream" => await target.Api.GenerateAsync(input, [], ToolChoice.Auto, config, _ => Task.CompletedTask, cts.Token),
                    "tools" => await target.Api.GenerateAsync(input, [WeatherTool], ToolChoice.Auto, config, cts.Token),
                    _ => await target.Api.GenerateAsync(input, [], ToolChoice.Auto, config, cts.Token),
                };
                if (generated.Output is { } output)
                {
                    result["ok"] = check != "tools" || output.Message.ToolCalls is { Count: > 0 };
                    result["output"] = OutputJson(output, target.Exchanges);
                }
                else
                {
                    result["error"] = AzureAIModelApi.AzureErrorMessage(generated.Error!);
                }
            }
            catch (OperationCanceledException)
            {
                result["error"] = "timed out after 120s";
            }
            catch (Exception ex) when (ex is RequestFailedException or ServiceResponseException or InvalidOperationException)
            {
                result["error"] = AzureAIModelApi.AzureErrorMessage(ex);
            }

            watch.Stop();
            result["ms"] = watch.ElapsedMilliseconds;
            result["exchanges"] = new JsonArray(target.Exchanges.Select(e => (JsonNode?)e.ToJson()).ToArray());
            return result;
        }

        /// <summary>The parsed output as the dashboard sees it: completion, reasoning (text and visibility), tool calls, usage.</summary>
        private static JsonObject OutputJson(ModelOutput output, IReadOnlyList<HttpExchange> exchanges)
        {
            const int maxReasoningChars = 2000;
            var reasoning = string.Join("\n\n", output.Message.ContentList.OfType<ContentReasoning>().Select(r => r.Redacted ? "[redacted thinking block]" : r.Reasoning));
            return new JsonObject
            {
                ["completion"] = output.Completion,
                ["stopReason"] = output.StopReason.ToWire(),
                ["reasoning"] = reasoning.Length == 0 ? null : reasoning.Length > maxReasoningChars ? reasoning[..maxReasoningChars] + $"… ({reasoning.Length:N0} chars)" : reasoning,
                ["reasoningVisibility"] = ReasoningVisibility(output, exchanges),
                ["toolCalls"] = output.Message.ToolCalls is { } calls
                    ? new JsonArray(calls.Select(c => (JsonNode?)new JsonObject { ["id"] = c.Id, ["function"] = c.Function, ["arguments"] = c.Arguments.DeepClone(), ["parseError"] = c.ParseError }).ToArray())
                    : null,
                ["usage"] = output.Usage is { } usage
                    ? new JsonObject
                    {
                        ["inputTokens"] = usage.InputTokens,
                        ["outputTokens"] = usage.OutputTokens,
                        ["totalTokens"] = usage.TotalTokens,
                        ["reasoningTokens"] = usage.ReasoningTokens,
                        ["cacheReadTokens"] = usage.InputTokensCacheRead,
                        ["cacheWriteTokens"] = usage.InputTokensCacheWrite,
                    }
                    : null,
            };
        }

        /// <summary>
        /// The route a discovered deployment takes: ARM's Anthropic format speaks the Messages API; an OpenAI-format
        /// deployment without chat completions (gpt-5.4-pro, codex) or whose name prefers it (gpt-5.6*, o-series) speaks
        /// the Responses API; everything else the model-inference route.
        /// </summary>
        private static string RouteFor(FoundryDeployment deployment) =>
            string.Equals(deployment.Format, "Anthropic", StringComparison.OrdinalIgnoreCase) ? "anthropic"
            : string.Equals(deployment.Format, "OpenAI", StringComparison.OrdinalIgnoreCase) && (!deployment.SupportsChat || OpenAIUtil.PrefersResponsesRoute(deployment.Name)) ? "responses"
            : "models";

        private static string? RouteNote(string route) => route switch
        {
            "anthropic" => "Anthropic Messages route (/anthropic/v1/messages)",
            "responses" => "OpenAI Responses route (/openai/v1/responses)",
            _ => null,
        };

        private static string ReasoningCell(SmokeRow row)
        {
            var verdict = Cell(row.Results, "reasoning");
            return row.ReasoningTokens is { } tokens && verdict is "text" or "hidden" ? $"{verdict} · {tokens} tok" : verdict;
        }

        private static string Cell(Dictionary<string, string> results, string check) =>
            results.TryGetValue(check, out var v) ? (v.StartsWith("fail", StringComparison.Ordinal) ? "FAIL" : v) : "-";

        private static async Task<string> SmokeAsync(CapturedTarget target, string check, string prompt, Func<GenerateConfig, GenerateConfig>? configure, SmokeRow row)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var watch = Stopwatch.StartNew();
            try
            {
                var input = new List<ChatMessage> { new ChatMessageUser(prompt) };
                var config = configure?.Invoke(DefaultConfig(target.Api)) ?? DefaultConfig(target.Api);
                GenerateResult result = check switch
                {
                    "stream" => await target.Api.GenerateAsync(input, [], ToolChoice.Auto, config, _ => Task.CompletedTask, cts.Token),
                    "tools" => await target.Api.GenerateAsync(input, [WeatherTool], ToolChoice.Auto, config, cts.Token),
                    _ => await target.Api.GenerateAsync(input, [], ToolChoice.Auto, config, cts.Token),
                };
                watch.Stop();
                row.Milliseconds += watch.ElapsedMilliseconds;
                if (result.Output is not { } output)
                {
                    row.Errors[check] = AzureAIModelApi.AzureErrorMessage(result.Error!);
                    return "fail";
                }

                row.Tokens += output.Usage?.TotalTokens ?? 0;
                if (check == "tools")
                {
                    var call = output.Message.ToolCalls?.FirstOrDefault();
                    if (call is null) return "no-call";
                    if (call.ParseError is not null) { row.Errors[check] = call.ParseError; return "fail"; }
                    return call.Function == WeatherTool.Name ? "ok" : "wrong-tool";
                }

                if (check == "reasoning")
                {
                    row.ReasoningTokens = output.Usage?.ReasoningTokens;
                    return ReasoningVisibility(output, target.Exchanges);
                }

                return output.Completion.Length > 0 || output.StopReason != StopReason.Unknown ? "ok" : "empty";
            }
            catch (OperationCanceledException)
            {
                row.Errors[check] = "timed out after 120s";
                return "fail";
            }
            catch (Exception ex) when (ex is RequestFailedException or ServiceResponseException or InvalidOperationException)
            {
                row.Errors[check] = AzureAIModelApi.AzureErrorMessage(ex);
                return "fail";
            }
        }

        private static void PrintResource(FoundryResource resource, string endpoint)
        {
            Console.WriteLine($"resource   : {resource.Name} ({resource.Kind}, {resource.Location})  resource group {resource.ResourceGroup}  subscription {resource.SubscriptionId}");
            Console.WriteLine($"endpoint   : {endpoint}{(resource.InferenceEndpoint is not null && !string.Equals(resource.InferenceEndpoint.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? $"  (ARM advertises {resource.InferenceEndpoint})" : "")}");
        }

        private sealed class SmokeRow(FoundryDeployment deployment)
        {
            public FoundryDeployment Deployment { get; } = deployment;
            public string? Skipped { get; set; }
            public Dictionary<string, string> Results { get; } = new();
            public Dictionary<string, string> Errors { get; } = new();
            public List<string> Notes { get; } = [];
            public int Tokens { get; set; }
            public int? ReasoningTokens { get; set; }
            public long Milliseconds { get; set; }
        }

        public static int Unknown(string command)
        {
            Console.Error.WriteLine($"unknown command '{command}'\n\n{Help}");
            return 2;
        }

        /// <summary>Sampling temperature for every command; null leaves the deployment default (gpt-5 models reject anything but 1).</summary>
        public static double? Temperature { get; set; }

        /// <summary>--max-tokens override (set when <see cref="MaxTokensSet"/> is true; null means do not send max_tokens).</summary>
        public static int? MaxTokens { get; set; }

        public static bool MaxTokensSet { get; set; }

        /// <summary>--reasoning-effort: Inspect's reasoning_effort (none|minimal|low|medium|high|xhigh|max), mapped per family.</summary>
        public static string? ReasoningEffort { get; set; }

        /// <summary>--reasoning-tokens: Inspect's reasoning_tokens budget (Claude budget_tokens, Cohere token_budget).</summary>
        public static int? ReasoningTokens { get; set; }

        /// <summary>--reasoning-summary: Inspect's reasoning_summary (none|concise|detailed|auto), sent as <c>reasoning.summary</c> on the Responses route.</summary>
        public static string? ReasoningSummary { get; set; }

        /// <summary>--model-arg key=value pairs merged into every created provider (the Python -M args).</summary>
        public static Dictionary<string, object?> ExtraModelArgs { get; } = new();

        private static GenerateConfig DefaultConfig(IModelApi api) =>
            new() { MaxTokens = MaxTokensSet ? MaxTokens : api.MaxTokens(), Temperature = Temperature, ReasoningEffort = ReasoningEffort, ReasoningTokens = ReasoningTokens, ReasoningSummary = ReasoningSummary };

        private static string Prompt(string prompt, string fallback = "This is a test string. What are you?") =>
            string.IsNullOrWhiteSpace(prompt) ? fallback : prompt;

        private static bool _streamingReasoning;

        private static readonly string Dim = Console.IsOutputRedirected ? "" : "\u001b[2m";

        private static readonly string Undim = Console.IsOutputRedirected ? "" : "\u001b[0m";

        private static Task OnStream(StreamEvent streamEvent)
        {
            if (streamEvent is not StreamReasoningEvent && _streamingReasoning)
            {
                Console.WriteLine();                       // reasoning ended: the answer starts on its own line
                _streamingReasoning = false;
            }

            switch (streamEvent)
            {
                case StreamReasoningEvent reasoning:
                    if (!_streamingReasoning)
                    {
                        Console.Write(Dim + "[reasoning] " + Undim);
                        _streamingReasoning = true;
                    }

                    Console.Write(Dim + reasoning.Reasoning + Undim);
                    break;
                case StreamTextEvent text:
                    Console.Write(text.Text);
                    break;
                case StreamToolCallEvent call:
                    Console.Write($"[tool_call id={call.Id} function={call.Function} args+='{call.Arguments}']");
                    break;
                default:
                    Console.Write($"[{streamEvent.Type}]");
                    break;
            }

            return Task.CompletedTask;
        }

        private static ChatMessageTool ExecuteTool(ToolCall call)
        {
            if (call.ParseError is not null)
            {
                Console.WriteLine($"tool call parse error: {call.ParseError}");
                return new ChatMessageTool("", call.Id, call.Function, new ToolCallError("parsing", call.ParseError));
            }

            if (call.Function != WeatherTool.Name)
            {
                return new ChatMessageTool("", call.Id, call.Function, new ToolCallError("unknown", $"Tool {call.Function} not found"));
            }

            var city = call.Arguments["city"]?.ToString() ?? "somewhere";
            var result = $"Weather in {city}: 21C, sunny, light breeze.";
            Console.WriteLine($"executed {call.Function}({call.Arguments.ToJsonString()}) -> {result}");
            return new ChatMessageTool(result, call.Id, call.Function);
        }

        private static int Report(GenerateResult result)
        {
            Console.WriteLine("-- model call request --");
            Console.WriteLine(result.Call.Request.ToJsonString(Pretty));
            Console.WriteLine("-- model call response --" + (result.Call.Error == true ? " (error)" : ""));
            Console.WriteLine(result.Call.Response?.ToJsonString(Pretty) ?? "(none)");
            if (result.Output is { } output)
            {
                Console.WriteLine($"-- output (model={output.Model}, stop_reason={output.StopReason.ToWire()}) --");
                Console.WriteLine(output.Completion);
                var reasoningItems = output.Message.ContentList.OfType<ContentReasoning>().ToList();
                if (reasoningItems.Count > 0)
                {
                    Console.WriteLine($"-- reasoning ({reasoningItems.Sum(r => r.Reasoning.Length)} chars, {reasoningItems.Count} block{(reasoningItems.Count == 1 ? "" : "s")}) --");
                    foreach (var item in reasoningItems)
                    {
                        Console.WriteLine(item.Redacted ? "[redacted thinking block]" : item.Reasoning.Length > 0 ? item.Reasoning : "[thinking block with a signature but no text]");
                    }
                }
                if (output.Message.ToolCalls is { Count: > 0 } calls)
                {
                    foreach (var call in calls)
                    {
                        Console.WriteLine($"tool_call {call.Id}: {call.Function}({call.Arguments.ToJsonString()}){(call.ParseError is null ? "" : " parse_error=" + call.ParseError)}");
                    }
                }

                if (output.Choices.Count > 0 && output.Choices[0].StopDetails is { } details)
                {
                    Console.WriteLine($"stop_details: type={details.Type} category={details.Category} explanation={details.Explanation}");
                }

                Console.WriteLine(output.Usage is { } usage
                    ? $"usage: input={usage.InputTokens} output={usage.OutputTokens} total={usage.TotalTokens}"
                      + (usage.ReasoningTokens is { } reasoningTokens ? $" reasoning={reasoningTokens}" : "")
                      + (usage.InputTokensCacheRead is { } cacheRead ? $" cache_read={cacheRead}" : "")
                      + (usage.InputTokensCacheWrite is { } cacheWrite ? $" cache_write={cacheWrite}" : "")
                    : "usage: (not reported)");
                return 0;
            }

            Console.WriteLine($"-- terminal error: {result.Error!.GetType().Name}: {AzureAIModelApi.AzureErrorMessage(result.Error)}");
            return 1;
        }

        private static async Task<string> MaterializeImage(string pathOrUrl)
        {
            if (pathOrUrl.StartsWith("data:", StringComparison.Ordinal))
            {
                return pathOrUrl;
            }

            byte[] bytes;
            string mime;
            if (pathOrUrl.StartsWith("http://", StringComparison.Ordinal) || pathOrUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                using var http = new HttpClient();
                using var response = await http.GetAsync(pathOrUrl);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();
                mime = response.Content.Headers.ContentType?.MediaType ?? MimeFromExtension(pathOrUrl);
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(pathOrUrl);
                mime = MimeFromExtension(pathOrUrl);
            }

            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        private static string MimeFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
    }

    /// <summary>A canned Azure endpoint for <c>--fake</c>: answers based on the request shape.</summary>
    internal static class FakeAzure
    {
        public static CannedTransport Transport() => new() { Responder = Respond };

        /// <summary>A credential handing out a fixed dummy token, so <c>--fake</c> never touches Azure.Identity.</summary>
        public static TokenCredential Credential { get; } = new FakeTokenCredential();

        private sealed class FakeTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new(GetToken(requestContext, cancellationToken));
        }

        private static Response Respond(CapturedRequest request)
        {
            var body = request.BodyJson;
            var messages = body["messages"]!.AsArray();
            var streaming = body["stream"]?.GetValue<bool>() == true;
            var hasToolResult = messages.Any(m => m!["role"]!.GetValue<string>() == "tool");
            var nativeTools = body.ContainsKey("tools");
            var hasImage = messages.Any(m => m!["content"] is JsonArray parts && parts.Any(p => p!["type"]!.GetValue<string>() == "image_url"));

            if (hasToolResult)
            {
                return Completion("The weather in Paris is 21C and sunny with a light breeze.", streaming);
            }

            if (body.ContainsKey("response_format"))
            {
                return Completion("""{"city":"Paris","country":"France","population_millions":2.1,"landmarks":["Eiffel Tower","Louvre","Notre-Dame"]}""", streaming);
            }

            if (nativeTools)
            {
                return streaming
                    ? CannedResponse.Sse(
                    [
                        Chunk("""{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_fake_1","type":"function","function":{"name":"get_weather","arguments":"{\"ci"}}]},"finish_reason":null}"""),
                        Chunk("""{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\": \"Paris\"}"}}]},"finish_reason":"tool_calls"}"""),
                        Usage(),
                    ])
                    : CannedResponse.Json(200,
                        """{"id":"fake-1","created":1,"model":"fake-model","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_fake_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\": \"Paris\"}"}}]}}],"usage":{"prompt_tokens":40,"completion_tokens":12,"total_tokens":52}}""");
            }

            if (hasImage)
            {
                return Completion("The image shows a small test picture.", streaming);
            }

            return Completion("I am a canned fake Azure AI endpoint standing in for a real model.", streaming);
        }

        private static Response Completion(string text, bool streaming)
        {
            if (!streaming)
            {
                return CannedResponse.Json(200, new JsonObject
                {
                    ["id"] = "fake-1",
                    ["created"] = 1,
                    ["model"] = "fake-model",
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["finish_reason"] = "stop",
                        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                        ["content_filter_results"] = new JsonObject { ["hate"] = new JsonObject { ["filtered"] = false, ["severity"] = "safe" } },
                    }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 30, ["completion_tokens"] = 15, ["total_tokens"] = 45 },
                }.ToJsonString());
            }

            var chunks = new List<string>();
            var words = text.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                var piece = JsonValue.Create(words[i] + (i < words.Length - 1 ? " " : "")).ToJsonString();
                var finish = i == words.Length - 1 ? "\"stop\"" : "null";
                chunks.Add(Chunk($$"""{"index":0,"delta":{"content":{{piece}}},"finish_reason":{{finish}}}"""));
            }

            chunks.Add(Usage());
            return CannedResponse.Sse(chunks);
        }

        private static string Chunk(string choice) =>
            $$"""{"id":"fake-1","created":1,"model":"fake-model","choices":[{{choice}}]}""";

        private static string Usage() =>
            """{"id":"fake-1","created":1,"model":"fake-model","choices":[],"usage":{"prompt_tokens":30,"completion_tokens":15,"total_tokens":45}}""";
    }

    /// <summary>One HTTP request/response pair as seen on the wire (secrets redacted), serialisable for the dashboard.</summary>
    internal sealed class HttpExchange(string method, string url)
    {
        private const int MaxBodyChars = 64 * 1024;

        private readonly long _started = Stopwatch.GetTimestamp();

        public string Method { get; } = method;

        public string Url { get; } = url;

        public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RequestBody { get; set; }

        public int Status { get; set; }

        public string? Reason { get; set; }

        public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? ResponseBody { get; set; }

        /// <summary>Set instead of <see cref="ResponseBody"/> for unbuffered (streamed) bodies; read once the consumer has drained it.</summary>
        public TeeStream? BodySource { get; set; }

        public long Milliseconds { get; private set; }

        public void Stop() => Milliseconds = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        /// <summary>Never records a credential: the bearer token is replaced by its length, key-style headers and cookies by a marker.</summary>
        public static string Redact(string name, string value)
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? $"Bearer <Entra ID token redacted, {value.Length - "Bearer ".Length} chars>"
                    : "<redacted>";
            }

            return name.Equals("api-key", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                ? "<redacted>"
                : value;
        }

        public JsonObject ToJson()
        {
            var body = ResponseBody ?? BodySource?.Captured;
            return new JsonObject
            {
                ["method"] = Method,
                ["url"] = Url,
                ["ms"] = Milliseconds,
                ["request"] = new JsonObject { ["headers"] = Headers(RequestHeaders), ["body"] = AsJsonOrText(RequestBody) },
                ["response"] = Status == 0
                    ? null
                    : new JsonObject { ["status"] = Status, ["reason"] = Reason, ["headers"] = Headers(ResponseHeaders), ["body"] = AsJsonOrText(body) },
            };
        }

        private static JsonObject Headers(Dictionary<string, string> headers)
        {
            var node = new JsonObject();
            foreach (var (name, value) in headers)
            {
                node[name] = value;
            }

            return node;
        }

        private static JsonNode? AsJsonOrText(string? text)
        {
            if (text is null)
            {
                return null;
            }

            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                }
            }

            return JsonValue.Create(text.Length > MaxBodyChars ? text[..MaxBodyChars] + $"\n… (truncated, {text.Length:N0} chars)" : text);
        }
    }

    /// <summary>
    /// Azure.Core pipeline policy recording every attempt the <c>ChatCompletionsClient</c> makes. Placed per-retry it
    /// sees the request after the bearer-token policy has added <c>Authorization</c>; buffered responses are read from
    /// <c>Response.Content</c>, streamed ones are tee'd so the SSE body is captured while the provider consumes it.
    /// </summary>
    internal sealed class HttpCapturePolicy : HttpPipelinePolicy
    {
        public List<HttpExchange> Exchanges { get; } = [];

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var exchange = Begin(message);
            try
            {
                ProcessNext(message, pipeline);
            }
            finally
            {
                Complete(message, exchange);
            }
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var exchange = Begin(message);
            try
            {
                await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            }
            finally
            {
                Complete(message, exchange);
            }
        }

        private HttpExchange Begin(HttpMessage message)
        {
            var exchange = new HttpExchange(message.Request.Method.Method, message.Request.Uri.ToString());
            foreach (var header in message.Request.Headers)
            {
                exchange.RequestHeaders[header.Name] = HttpExchange.Redact(header.Name, header.Value);
            }

            if (message.Request.Content is { } content)
            {
                using var buffer = new MemoryStream();
                content.WriteTo(buffer, CancellationToken.None);
                exchange.RequestBody = Encoding.UTF8.GetString(buffer.ToArray());
            }

            Exchanges.Add(exchange);
            return exchange;
        }

        private static void Complete(HttpMessage message, HttpExchange exchange)
        {
            exchange.Stop();
            if (!message.HasResponse)
            {
                return;
            }

            var response = message.Response;
            exchange.Status = response.Status;
            exchange.Reason = response.ReasonPhrase;
            foreach (var header in response.Headers)
            {
                exchange.ResponseHeaders[header.Name] = HttpExchange.Redact(header.Name, header.Value);
            }

            if (message.BufferResponse)
            {
                exchange.ResponseBody = response.Content.ToString();
            }
            else if (response.ContentStream is { } stream)
            {
                var tee = new TeeStream(stream);
                response.ContentStream = tee;
                exchange.BodySource = tee;
            }
        }
    }

    /// <summary>The same recording for the Anthropic route, which uses <see cref="HttpClient"/> over an injectable handler.</summary>
    internal sealed class HttpCaptureHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public List<HttpExchange> Exchanges { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var exchange = new HttpExchange(request.Method.Method, request.RequestUri?.ToString() ?? "");
            foreach (var (name, values) in request.Headers)
            {
                exchange.RequestHeaders[name] = HttpExchange.Redact(name, string.Join(", ", values));
            }

            if (request.Content is { } content)
            {
                foreach (var (name, values) in content.Headers)
                {
                    exchange.RequestHeaders[name] = string.Join(", ", values);
                }

                exchange.RequestBody = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            Exchanges.Add(exchange);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                exchange.Stop();
            }

            exchange.Status = (int)response.StatusCode;
            exchange.Reason = response.ReasonPhrase;
            foreach (var (name, values) in response.Headers)
            {
                exchange.ResponseHeaders[name] = HttpExchange.Redact(name, string.Join(", ", values));
            }

            foreach (var (name, values) in response.Content.Headers)
            {
                exchange.ResponseHeaders[name] = string.Join(", ", values);
            }

            var tee = new TeeStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var replacement = new StreamContent(tee);
            foreach (var (name, values) in response.Content.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(name, values);
            }

            response.Content = replacement;
            exchange.BodySource = tee;
            return response;
        }
    }

    /// <summary>A read-only pass-through stream that keeps a copy of everything read through it.</summary>
    internal sealed class TeeStream(Stream inner) : Stream
    {
        private readonly MemoryStream _copy = new();

        public string Captured => Encoding.UTF8.GetString(_copy.ToArray());

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _copy.Write(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _copy.Write(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

