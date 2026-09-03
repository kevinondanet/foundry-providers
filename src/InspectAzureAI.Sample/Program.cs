using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Provider;
using System.Diagnostics;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Sample;

var arguments = args.ToList();
var fake = arguments.Remove("--fake");
var streamingArg = TakeOption(arguments, "--streaming");
var emulateArg = TakeOption(arguments, "--emulate-tools");
var modelArg = TakeOption(arguments, "--model");
var authArg = TakeOption(arguments, "--auth");
var routeArg = TakeOption(arguments, "--route");
var jsonFlag = arguments.Remove("--json");
var includeFailed = arguments.Remove("--include-failed");
var skipTools = arguments.Remove("--skip-tools");
var onlyArg = TakeOption(arguments, "--only");
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

    Cli.ExtraModelArgs[pair[..eq]] = Cli.ParseModelArgValue(pair[(eq + 1)..]);
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
        "config" => Cli.Config(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg)),
        "naming" => Cli.Naming(rest),
        "chat" => await Cli.Chat(Cli.CreateModelApi(routeArg, modelArg, streamingArg, emulateArg, fake, authArg), string.Join(" ", rest)),
        "stream" => await Cli.Stream(Cli.CreateModelApi(routeArg, modelArg, streamingArg, emulateArg, fake, authArg), string.Join(" ", rest)),
        "tools" => await Cli.ToolLoop(Cli.CreateModelApi(routeArg, modelArg, streamingArg, emulateArg ?? "false", fake, authArg), string.Join(" ", rest)),
        "emulate-tools" => await Cli.ToolLoop(Cli.CreateModelApi(routeArg, modelArg, streamingArg, emulateArg ?? "true", fake, authArg), string.Join(" ", rest)),
        "image" => await Cli.Image(Cli.CreateModelApi(routeArg, modelArg, streamingArg, emulateArg, fake, authArg), rest),
        "retry-demo" => Cli.RetryDemo(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg)),
        "token" => await Cli.Token(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg)),
        "models" => await Cli.Models(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg), authArg, fake, jsonFlag),
        "test-all" => await Cli.TestAll(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg), authArg, fake, onlyArg, includeFailed, skipTools, jsonFlag),
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
    Console.Error.WriteLine(ex.Message);
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
    internal static class Cli
    {
        public const string Help = """
            InspectAzureAI.Sample - .NET port of the Inspect AI `azureai` model provider

            usage: dotnet run --project src/InspectAzureAI.Sample -- <command> [options] [args]

            commands:
              config                    print the resolved endpoint, auth mode and model names (no call)
              naming <model...>         show service/canonical names and is-llama for each model name
              chat [prompt]             one non-streaming completion
              stream [prompt]           one streaming completion (deltas printed as they arrive)
              tools [prompt]            native function-calling loop with the local get_weather tool
              emulate-tools [prompt]    Llama <tool_call> prompt-format tool-calling loop, same tool
              image <path-or-url>       send an image (materialised as a data URI) with a question
              retry-demo                show ShouldRetry / IsAuthFailure / HandleAzureError decisions
              token                     acquire an Entra ID token with the resolved credential and print
                                        who it belongs to (verifies that `az login` is picked up; no model call)
              models                    discover the Foundry resource behind AZUREAI_BASE_URL through Azure
                                        Resource Manager and list its model deployments (--json for machines)
              test-all                  smoke-test every healthy chat deployment: chat, stream, native tools; Anthropic
                                        deployments go through the Messages route automatically
                                        (--only a,b  --include-failed  --skip-tools  --json); exit 1 on any chat failure
              --help                    this text

            options:
              --model <name>            model name (default: $INSPECT_AZUREAI_MODEL or Llama-3.3-70B-Instruct)
              --streaming auto|true|false   the `streaming` model arg (default auto)
              --emulate-tools true|false    the `emulate_tools` model arg
              --fake                    answer from a canned in-memory transport (no network, no keys)
              --temperature <n>         sampling temperature (default: not sent; gpt-5 deployments accept only 1)
              --max-tokens <n|none>     max_tokens sent (default: the provider's max_tokens(), 2048 for most models)
              --model-arg key=value     repeatable; the Python -M model args (emulate_tools, max_completion_tokens,
                                        streaming, or any pass-through body field such as safe_mode=true)
              --auth <selector>         Entra ID credential: default (DefaultAzureCredential, includes az login),
                                        cli, developer-cli, managed-identity, environment, interactive
              --route models|anthropic  chat/stream/tools/image: the model-inference route (default) or the
                                        Anthropic Messages route (/anthropic/v1/messages) for Claude deployments

            environment (same names and precedence as the Python provider):
              AZURE_API_KEY / AZUREAI_API_KEY                       api key (legacy name wins)
              AZURE_ENDPOINT_URL / AZUREAI_ENDPOINT_URL / AZUREAI_BASE_URL   endpoint (in that order)
              INSPECT_EVAL_MODEL_BASE_URL                           last-resort endpoint fallback
              AZUREAI_AUDIENCE                                      Entra ID token scope (default https://cognitiveservices.azure.com/.default)
              AZUREAI_CREDENTIAL                                    same values as --auth (default: default)
              AZURE_TENANT_ID / AZURE_CLIENT_ID                     tenant pin / user-assigned managed identity
              AZUREAI_RESOURCE_ID / AZURE_SUBSCRIPTION_ID           models/test-all: skip or narrow the ARM search
              AZUREAI_ANTHROPIC_API_KEY / AZUREAI_ANTHROPIC_BASE_URL  Anthropic route (Inspect's names); the base URL is
                                                            derived from AZUREAI_BASE_URL when unset

            Entra ID is used whenever no API key is set: sign in with `az login` (and `az account set`),
            then run `token` to confirm which identity the credential resolves to.
            """;

        /// <summary>What to try when token acquisition fails.</summary>
        public const string LoginHint = """
            Hints:
              az login                                   sign in (add --tenant <id> for a specific tenant)
              az account set --subscription <name|id>    pick the subscription that owns the endpoint
              --auth cli / AZUREAI_CREDENTIAL=cli        skip the managed-identity probe and use az login directly
              AZURE_TENANT_ID=<id>                       pin the tenant for the default / cli credentials
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

        public static AzureAIModelApi CreateApi(string? model, string? streaming, string? emulateTools, bool fake, string? auth = null)
        {
            var settings = new AzureAIClientSettings();
            if (auth is not null)
            {
                try
                {
                    settings = settings with { TokenCredential = AzureHosting.CreateCredential(auth) };
                }
                catch (PrerequisiteError ex)
                {
                    throw new UsageError($"--auth: {ex.Message}");
                }
            }

            model ??= Environment.GetEnvironmentVariable("INSPECT_AZUREAI_MODEL") ?? "Llama-3.3-70B-Instruct";
            var modelArgs = new Dictionary<string, object?>(ExtraModelArgs);
            if (emulateTools is not null)
            {
                if (!bool.TryParse(emulateTools, out var emulate))
                {
                    throw new UsageError($"--emulate-tools expects true or false, got '{emulateTools}'");
                }

                modelArgs["emulate_tools"] = emulate;
            }

            try
            {
                if (fake)
                {
                    return new AzureAIModelApi(model, "https://fake.local/models", "fake-key", streaming: streaming, modelArgs: modelArgs,
                        settings: new AzureAIClientSettings { Transport = FakeAzure.Transport() });
                }

                return new AzureAIModelApi(model, streaming: streaming, modelArgs: modelArgs, settings: settings);
            }
            catch (ArgumentException ex)
            {
                // NormalizeStreamArg rejects anything but auto/true/false with the Python message.
                throw new UsageError($"--streaming: {ex.Message}");
            }
        }

        /// <summary>Creates the provider for the selected route: model-inference (default) or the Anthropic Messages route.</summary>
        public static IModelApi CreateModelApi(string? route, string? model, string? streaming, string? emulateTools, bool fake, string? auth)
        {
            if (route is null || route.Equals("models", StringComparison.OrdinalIgnoreCase))
            {
                return CreateApi(model, streaming, emulateTools, fake, auth);
            }

            if (!route.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageError($"--route expects models or anthropic, got '{route}'");
            }

            if (fake)
            {
                throw new UsageError("--route anthropic has no --fake endpoint");
            }

            var settings = new AzureAIClientSettings();
            if (auth is not null)
            {
                try
                {
                    settings = settings with { TokenCredential = AzureHosting.CreateCredential(auth) };
                }
                catch (PrerequisiteError ex)
                {
                    throw new UsageError($"--auth: {ex.Message}");
                }
            }

            try
            {
                return new AnthropicFoundryModelApi(model ?? Environment.GetEnvironmentVariable("INSPECT_AZUREAI_MODEL") ?? "claude-sonnet-4-6", streaming: streaming, settings: settings);
            }
            catch (ArgumentException ex)
            {
                throw new UsageError($"--streaming: {ex.Message}");
            }
        }

        public static int Config(AzureAIModelApi api)
        {
            Console.WriteLine($"endpoint            : {api.EndpointUrl}");
            Console.WriteLine($"auth mode           : {(api.ApiKey is not null ? "api key (sent as api-key + Authorization: Bearer)" : "Entra ID (Authorization: Bearer only)")}");
            if (api.Credential is not null)
            {
                Console.WriteLine($"credential          : {AzureHosting.Describe(api.Credential)}");
                Console.WriteLine("                      run `token` to confirm the sign-in (az login) yields a token");
            }
            Console.WriteLine($"model_name          : {api.ModelName}");
            Console.WriteLine($"service_model_name  : {api.ServiceModelName()}");
            Console.WriteLine($"canonical_name      : {api.CanonicalName()}");
            Console.WriteLine($"org_prefix          : {api.OrgPrefix ?? "(none)"}");
            Console.WriteLine($"is_llama / mistral  : {api.IsLlama()} / {api.IsMistral()}");
            Console.WriteLine($"max_tokens()        : {(api.MaxTokens()?.ToString() ?? "null (server default)")}");
            Console.WriteLine($"streaming           : {(api.Streaming?.ToString() ?? "auto")}");
            Console.WriteLine($"emulate_tools       : {(api.EmulateTools?.ToString() ?? "auto (Llama only)")}");
            Console.WriteLine($"connection_key      : {api.ConnectionKey()}");
            Console.WriteLine($"model_extras        : {JsonSerializer.Serialize(api.ModelArgs)}");
            return 0;
        }

        public static int Naming(List<string> models)
        {
            if (models.Count == 0)
            {
                models = ["gpt-4o", "o1-preview", "Mistral-large-2411", "Llama-3.3-70B-Instruct", "moonshotai/kimi-k2.5", "my-custom-org/gpt-4o", "custom-org/llama-3-70b"];
            }

            Console.WriteLine($"{"model_name",-28} {"service",-24} {"canonical",-28} {"llama",-6} {"mistral",-8} max_tokens");
            foreach (var model in models)
            {
                var api = new AzureAIModelApi(model, "https://example.com/models", "key");
                Console.WriteLine($"{model,-28} {api.ServiceModelName(),-24} {api.CanonicalName(),-28} {api.IsLlama(),-6} {api.IsMistral(),-8} {api.MaxTokens()?.ToString() ?? "null"}");
            }

            return 0;
        }

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
            for (var turn = 0; turn < 5; turn++)
            {
                Console.WriteLine($"== turn {turn + 1} ({(api is AzureAIModelApi azure ? $"emulate_tools={azure.EmulateTools?.ToString() ?? "auto"}" : "Anthropic Messages route")}) ==");
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
            if (api.Credential is null)
            {
                Console.WriteLine("An API key is configured, so Entra ID is not used. Unset AZURE_API_KEY / AZUREAI_API_KEY to sign in with az login.");
                return 0;
            }

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

        private static readonly (string Check, string Prompt)[] SmokeChecks =
        [
            ("chat", "Reply with exactly: ok"),
            ("stream", "Count from 1 to 3 on one line."),
            ("tools", "What is the weather in Oslo right now? Use the get_weather tool."),
        ];

        private static TokenCredential ArmCredential(AzureAIModelApi api, string? auth) =>
            api.Credential?.Inner ?? api.Settings.TokenCredential ?? AzureHosting.CreateCredential(auth);

        /// <summary>Discovers the resource behind the endpoint and prints its deployments.</summary>
        public static async Task<int> Models(AzureAIModelApi api, string? auth, bool fake, bool json)
        {
            if (fake)
            {
                Console.Error.WriteLine("models needs Azure Resource Manager; it is not available with --fake.");
                return 2;
            }

            using var catalog = new FoundryCatalog(ArmCredential(api, auth));
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
        public static async Task<int> TestAll(AzureAIModelApi api, string? auth, bool fake, string? only, bool includeFailed, bool skipTools, bool json)
        {
            if (fake)
            {
                Console.Error.WriteLine("test-all needs Azure Resource Manager; it is not available with --fake.");
                return 2;
            }

            using var catalog = new FoundryCatalog(ArmCredential(api, auth));
            var (resource, deployments) = await catalog.DiscoverAsync(api.EndpointUrl);
            var wanted = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shared = api.Settings with { TokenCredential = api.Credential?.Inner ?? api.Settings.TokenCredential };   // one credential, one token cache
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
                if (!includeFailed && (!deployment.IsSucceeded || !deployment.SupportsChat))
                {
                    row.Skipped = !deployment.IsSucceeded ? $"provisioningState={deployment.State}" : "chatCompletion=false";
                    if (!json) Console.WriteLine($"{deployment.Name,-22} skipped ({row.Skipped})");
                    continue;
                }

                var anthropic = IsAnthropicFormat(deployment);
                IModelApi target = anthropic
                    ? new AnthropicFoundryModelApi(deployment.Name, AnthropicFoundryModelApi.DeriveBaseUrl(api.EndpointUrl), streaming: api.Streaming, settings: shared)
                    : new AzureAIModelApi(deployment.Name, api.EndpointUrl, streaming: api.Streaming, modelArgs: ExtraModelArgs, settings: shared);
                if (anthropic)
                {
                    row.Notes.Add("Anthropic Messages route (/anthropic/v1/messages)");
                }

                var forcedMaxCompletionTokens = false;
                foreach (var (check, prompt) in SmokeChecks)
                {
                    if (check == "tools" && skipTools)
                    {
                        row.Results[check] = "skipped";
                        continue;
                    }

                    var status = await SmokeAsync(target, check, prompt, row);
                    if (status == "fail" && !anthropic && !forcedMaxCompletionTokens
                        && row.Errors.GetValueOrDefault(check, "").Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
                    {
                        // Reasoning models (e.g. MAI-Thinking-1) reject max_tokens; Python's name rule does not know them.
                        var args = new Dictionary<string, object?>(ExtraModelArgs) { ["max_completion_tokens"] = true };
                        target = new AzureAIModelApi(deployment.Name, api.EndpointUrl, streaming: api.Streaming, modelArgs: args, settings: shared);
                        forcedMaxCompletionTokens = true;
                        row.Notes.Add("needs --model-arg max_completion_tokens=true");
                        row.Errors.Remove(check);
                        status = await SmokeAsync(target, check, prompt, row);
                    }

                    row.Results[check] = status;
                }

                if (!json)
                {
                    Console.WriteLine($"{deployment.Name,-22} chat={row.Results.GetValueOrDefault("chat")} stream={row.Results.GetValueOrDefault("stream")} tools={row.Results.GetValueOrDefault("tools")} tokens={row.Tokens} ms={row.Milliseconds}{(row.Notes.Count > 0 ? "  " + string.Join("; ", row.Notes) : "")}");
                }
            }

            var tested = rows.Where(r => r.Skipped is null).ToList();
            var failedChat = tested.Where(r => r.Results.GetValueOrDefault("chat") != "ok").ToList();
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    resource, endpoint = api.EndpointUrl,
                    results = rows.Select(r => new { deployment = r.Deployment.Name, format = r.Deployment.Format, state = r.Deployment.State, skipped = r.Skipped, checks = r.Results, tokens = r.Tokens, ms = r.Milliseconds, notes = r.Notes, errors = r.Errors }),
                    ok = failedChat.Count == 0,
                }, Pretty));
                return failedChat.Count == 0 ? 0 : 1;
            }

            Console.WriteLine();
            Console.WriteLine($"{"deployment",-30} {"format",-11} {"state",-10} {"chat",-8} {"stream",-8} {"tools",-8} {"tokens",7} {"ms",7}  note");
            foreach (var r in rows)
            {
                Console.WriteLine(r.Skipped is not null
                    ? $"{r.Deployment.Name,-30} {r.Deployment.Format,-11} {r.Deployment.State,-10} skipped: {r.Skipped}"
                    : $"{r.Deployment.Name,-30} {r.Deployment.Format,-11} {r.Deployment.State,-10} {Cell(r.Results, "chat"),-8} {Cell(r.Results, "stream"),-8} {Cell(r.Results, "tools"),-8} {r.Tokens,7} {r.Milliseconds,7}  {string.Join("; ", r.Notes)}");
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

        private static bool IsAnthropicFormat(FoundryDeployment deployment) =>
            string.Equals(deployment.Format, "Anthropic", StringComparison.OrdinalIgnoreCase);

        private static string Cell(Dictionary<string, string> results, string check) =>
            results.TryGetValue(check, out var v) ? (v.StartsWith("fail", StringComparison.Ordinal) ? "FAIL" : v) : "-";

        private static async Task<string> SmokeAsync(IModelApi target, string check, string prompt, SmokeRow row)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var watch = Stopwatch.StartNew();
            try
            {
                var input = new List<ChatMessage> { new ChatMessageUser(prompt) };
                var config = DefaultConfig(target);
                GenerateResult result = check switch
                {
                    "stream" => await target.GenerateAsync(input, [], ToolChoice.Auto, config, _ => Task.CompletedTask, cts.Token),
                    "tools" => await target.GenerateAsync(input, [WeatherTool], ToolChoice.Auto, config, cts.Token),
                    _ => await target.GenerateAsync(input, [], ToolChoice.Auto, config, cts.Token),
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

        /// <summary>--model-arg key=value pairs merged into every created provider (the Python -M args).</summary>
        public static Dictionary<string, object?> ExtraModelArgs { get; } = new();

        /// <summary>Parses a -M value the way YAML would: true/false, integers, decimals, else a string.</summary>
        public static object? ParseModelArgValue(string value) =>
            bool.TryParse(value, out var b) ? b
            : int.TryParse(value, out var i) ? i
            : double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
            : value;

        private static GenerateConfig DefaultConfig(IModelApi api) =>
            new() { MaxTokens = MaxTokensSet ? MaxTokens : api.MaxTokens(), Temperature = Temperature };

        private static string Prompt(string prompt, string fallback = "This is a test string. What are you?") =>
            string.IsNullOrWhiteSpace(prompt) ? fallback : prompt;

        private static Task OnStream(StreamEvent streamEvent)
        {
            switch (streamEvent)
            {
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

        private static Response Respond(CapturedRequest request)
        {
            var body = request.BodyJson;
            var messages = body["messages"]!.AsArray();
            var streaming = body["stream"]?.GetValue<bool>() == true;
            var hasToolResult = messages.Any(m => m!["role"]!.GetValue<string>() == "tool");
            var nativeTools = body.ContainsKey("tools");
            var emulated = messages.Count > 0 && messages[0]!["role"]!.GetValue<string>() == "system"
                           && (messages[0]!["content"]?.GetValue<string>() ?? "").Contains("\"name\": \"get_weather\"");
            var hasImage = messages.Any(m => m!["content"] is JsonArray parts && parts.Any(p => p!["type"]!.GetValue<string>() == "image_url"));

            if (hasToolResult)
            {
                return Completion("The weather in Paris is 21C and sunny with a light breeze.", streaming);
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

            if (emulated)
            {
                return Completion("Let me look that up.<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>", streaming);
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
}
