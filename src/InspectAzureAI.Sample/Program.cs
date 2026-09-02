using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Testing;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Sample;

var arguments = args.ToList();
var fake = arguments.Remove("--fake");
var streamingArg = TakeOption(arguments, "--streaming");
var emulateArg = TakeOption(arguments, "--emulate-tools");
var modelArg = TakeOption(arguments, "--model");
var authArg = TakeOption(arguments, "--auth");

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
        "chat" => await Cli.Chat(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg), string.Join(" ", rest)),
        "stream" => await Cli.Stream(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg), string.Join(" ", rest)),
        "tools" => await Cli.ToolLoop(Cli.CreateApi(modelArg, streamingArg, emulateArg ?? "false", fake, authArg), string.Join(" ", rest)),
        "emulate-tools" => await Cli.ToolLoop(Cli.CreateApi(modelArg, streamingArg, emulateArg ?? "true", fake, authArg), string.Join(" ", rest)),
        "image" => await Cli.Image(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg), rest),
        "retry-demo" => Cli.RetryDemo(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg)),
        "token" => await Cli.Token(Cli.CreateApi(modelArg, streamingArg, emulateArg, fake, authArg)),
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
              --help                    this text

            options:
              --model <name>            model name (default: $INSPECT_AZUREAI_MODEL or Llama-3.3-70B-Instruct)
              --streaming auto|true|false   the `streaming` model arg (default auto)
              --emulate-tools true|false    the `emulate_tools` model arg
              --fake                    answer from a canned in-memory transport (no network, no keys)
              --auth <selector>         Entra ID credential: default (DefaultAzureCredential, includes az login),
                                        cli, developer-cli, managed-identity, environment, interactive

            environment (same names and precedence as the Python provider):
              AZURE_API_KEY / AZUREAI_API_KEY                       api key (legacy name wins)
              AZURE_ENDPOINT_URL / AZUREAI_ENDPOINT_URL / AZUREAI_BASE_URL   endpoint (in that order)
              INSPECT_EVAL_MODEL_BASE_URL                           last-resort endpoint fallback
              AZUREAI_AUDIENCE                                      Entra ID token scope (default https://cognitiveservices.azure.com/.default)
              AZUREAI_CREDENTIAL                                    same values as --auth (default: default)
              AZURE_TENANT_ID / AZURE_CLIENT_ID                     tenant pin / user-assigned managed identity

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
            var modelArgs = new Dictionary<string, object?>();
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

        public static async Task<int> Chat(AzureAIModelApi api, string prompt)
        {
            var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt)) };
            var result = await api.GenerateAsync(input, [], ToolChoice.Auto, DefaultConfig(api));
            return Report(result);
        }

        public static async Task<int> Stream(AzureAIModelApi api, string prompt)
        {
            var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt)) };
            Console.WriteLine("-- stream events --");
            var result = await api.GenerateAsync(input, [], ToolChoice.Auto, DefaultConfig(api), OnStream);
            Console.WriteLine();
            return Report(result);
        }

        public static async Task<int> ToolLoop(AzureAIModelApi api, string prompt)
        {
            var input = new List<ChatMessage> { new ChatMessageUser(Prompt(prompt, "What is the weather like in Paris right now? Use the get_weather tool.")) };
            for (var turn = 0; turn < 5; turn++)
            {
                Console.WriteLine($"== turn {turn + 1} (emulate_tools={api.EmulateTools?.ToString() ?? "auto"}) ==");
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

        public static async Task<int> Image(AzureAIModelApi api, List<string> rest)
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

        public static int Unknown(string command)
        {
            Console.Error.WriteLine($"unknown command '{command}'\n\n{Help}");
            return 2;
        }

        private static GenerateConfig DefaultConfig(AzureAIModelApi api) =>
            new() { MaxTokens = api.MaxTokens(), Temperature = 0.0 };

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
