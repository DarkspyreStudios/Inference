using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Darkspyre.Inference.Connect.LLM.Vertex;
using Microsoft.Extensions.DependencyInjection;
using SecureStore;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal static class Program
{
    // Preserve the original LlmConnect service name so existing SecureStore credentials remain accessible.
    private const string CredentialServiceName = "com.darkspyrestudios.llmconnect.console-tester";
    private static readonly JsonSerializerOptions UsageJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            WriteHelp();
            return args.Length == 0 ? 2 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var services = new ServiceCollection();
        services.AddSecureStore(options =>
        {
            options.ServiceName = CredentialServiceName;
            options.Label = "Darkspyre.Inference.Connect.LLM Console Tester";
        });
        services.AddSingleton<ConsoleTesterProviders>();
        services.AddSingleton(container => ConsoleInvocation.Parse(args,
            container.GetRequiredService<ConsoleTesterProviders>()));
        services.AddSingleton<SecureConnectionSource>();
        services.AddSingleton<ILlmConnectionSource>(provider =>
            provider.GetRequiredService<SecureConnectionSource>());
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        services.AddLlmConnectVertex();
        await using ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            ConsoleTesterProviders catalog = provider.GetRequiredService<ConsoleTesterProviders>();
            if (args[0] == "providers")
            {
                if (args.Length != 1) throw new ArgumentException("providers does not accept options.");
                foreach (var entry in catalog.All)
                    Console.WriteLine($"{entry.Definition.Id}\t{entry.DisplayName}\t(alias: {entry.Id})");
                return 0;
            }
            ConsoleInvocation invocation = provider.GetRequiredService<ConsoleInvocation>();
            LlmProviderService llms = provider.GetRequiredService<LlmProviderService>();
            LlmAgentProviderService agent = provider.GetRequiredService<LlmAgentProviderService>();
            return invocation.Command switch
            {
                "setup" => await RunSetupAsync(llms, invocation, cancellation.Token),
                "apple-probe" => await RunAppleProbeAsync(llms, agent, invocation, cancellation.Token),
                "vertex-probe" when invocation.Provider.Transport == LlmTransport.GoogleVertexAi =>
                    await GoogleProbe.RunAsync(llms, agent, invocation, cancellation.Token),
                "gemini-probe" when invocation.Provider.Transport == LlmTransport.GoogleGeminiApi =>
                    await GoogleProbe.RunAsync(llms, agent, invocation, cancellation.Token),
                "login" => await RunLoginAsync(llms, provider.GetRequiredService<ILlmConnectionSource>(), invocation, cancellation.Token),
                "status" => await RunStatusAsync(llms, invocation, cancellation.Token),
                "models" => await RunModelsAsync(llms, invocation, cancellation.Token),
                "usage" => await RunUsageAsync(llms, invocation, cancellation.Token),
                "prompt" => await RunPromptAsync(llms, invocation, cancellation.Token),
                "agy-probe" when invocation.Provider.Transport == LlmTransport.GoogleAntigravityCli =>
                    await AntigravityProbe.RunAsync(llms, agent, invocation, cancellation.Token),
                "codex-probe" when invocation.Provider.Transport == LlmTransport.OpenAiCodex =>
                    await CodexProtocolProbe.RunAsync(llms, invocation.Provider.ConnectionId,
                        ParsePromptOptions(invocation.CommandArguments).Model, cancellation.Token),
                "managed-probe" when invocation.Provider.Transport == LlmTransport.OpenAiCodex =>
                    await ManagedAgentProbe.RunAsync(agent, invocation.Provider.ConnectionId,
                        ParsePromptOptions(invocation.CommandArguments).Model, cancellation.Token),
                "agent" => await RunAgentAsync(
                    llms,
                    agent,
                    invocation,
                    cancellation.Token),
                _ => UnknownCommand(invocation.Command)
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (InferenceException exception)
        {
            Console.Error.WriteLine($"Inference error [{exception.Code}].");
            return 1;
        }
        catch (LlmProviderException exception)
        {
            Console.Error.WriteLine($"Provider error [{exception.ProviderCode ?? "unknown"}]: {exception.Message}");
            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is FormatException
                                              or InvalidOperationException
                                              or NotSupportedException
                                              or IOException
                                              or JsonException
                                              or SecureStoreException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunLoginAsync(
        LlmProviderService llms,
        ILlmConnectionSource connections,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Provider.Transport == LlmTransport.AppleFoundationModels)
            throw new ArgumentException("Apple requires no login. Use status --provider apple [--helper <path>].");
        if (invocation.Provider.Transport == LlmTransport.GoogleAntigravityCli)
        {
            RequireNoCommandArguments(invocation);
            bool antigravityReady = await llms.IsAuthenticatedAsync(
                invocation.Provider.ConnectionId,
                cancellationToken);
            Console.WriteLine(antigravityReady
                ? "Antigravity CLI is authenticated."
                : "Antigravity CLI is not authenticated. Run agy in a terminal and complete Google sign-in.");
            return antigravityReady ? 0 : 3;
        }

        if (invocation.Provider.Transport is LlmTransport.AwsBedrockConverse
            or LlmTransport.AwsBedrockAgentCore)
        {
            BedrockLoginOptions options = ParseBedrockLoginOptions(invocation.CommandArguments);
            LlmConnection connection = await connections.GetConnectionAsync(invocation.Provider.ConnectionId, cancellationToken)
                ?? throw new InvalidOperationException($"Connection '{invocation.Provider.ConnectionId}' is not configured.");
            await llms.SetBedrockCredentialsAsync(
                invocation.Provider.ConnectionId,
                new LlmAwsCredentials
                {
                    Profile = options.Profile,
                    Region = options.Region
                },
                connection.Version,
                cancellationToken);
            if (invocation.Provider.Transport == LlmTransport.AwsBedrockConverse)
            {
                IReadOnlyList<LlmModel> models = await llms.ListModelsAsync(
                    invocation.Provider.ConnectionId,
                    cancellationToken);
                Console.WriteLine(
                    $"Amazon Bedrock profile '{options.Profile}' is configured in {options.Region}; discovered {models.Count} model(s).");
            }
            else
            {
                Console.WriteLine(
                    $"AgentCore Harness is configured with AWS profile '{options.Profile}' in {options.Region}. Use the agent command to invoke it.");
            }
            return 0;
        }

        if (invocation.Provider.Transport == LlmTransport.GoogleGeminiApi)
        {
            RequireNoCommandArguments(invocation);
            // The key is read from the environment so it never appears in a command argument or shell history.
            string key = Environment.GetEnvironmentVariable("GEMINI_API_KEY") is { Length: > 0 } value ? value
                : throw new ArgumentException("Set GEMINI_API_KEY to the Google AI Studio key, then run login --provider gemini.");
            LlmConnection current = await connections.GetConnectionAsync(invocation.Provider.ConnectionId, cancellationToken)
                ?? throw new InvalidOperationException($"Connection '{invocation.Provider.ConnectionId}' is not configured.");
            await llms.SetApiKeyAsync(invocation.Provider.ConnectionId, key, current.Version, cancellationToken);
            IReadOnlyList<LlmModel> models = await llms.ListModelsAsync(invocation.Provider.ConnectionId, cancellationToken);
            Console.WriteLine($"{invocation.Provider.DisplayName} key is stored in the operating system vault; discovered {models.Count} model(s).");
            return 0;
        }

        RequireNoCommandArguments(invocation);
        await llms.SignInAsync(
            invocation.Provider.ConnectionId,
            new ConsoleAuthPrompt(),
            cancellationToken);

        bool ready = await llms.IsAuthenticatedAsync(
            invocation.Provider.ConnectionId,
            cancellationToken);
        if (ready)
        {
            string detail = invocation.Provider.Transport switch
            {
                LlmTransport.OpenAiCodex => "its direct ChatGPT OAuth credential is stored in the operating system vault.",
                LlmTransport.AnthropicClaudeCli => "the Claude CLI owns its credential.",
                _ => "its host credential is stored in the operating system vault."
            };
            Console.WriteLine($"{invocation.Provider.DisplayName} is authenticated; {detail}");
            return 0;
        }

        Console.Error.WriteLine(
            $"{invocation.Provider.DisplayName} authorization was saved, but its external CLI is not ready.");
        return 3;
    }

    private static async Task<int> RunStatusAsync(
        LlmProviderService llms,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        RequireNoCommandArguments(invocation);
        return await CheckReadyAsync(llms, invocation, cancellationToken) ? 0 : 3;
    }

    internal static async Task<bool> CheckReadyAsync(LlmProviderService llms,
        ConsoleInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Provider.Transport is LlmTransport.AppleFoundationModels or LlmTransport.GoogleVertexAi)
            return await ConsoleSetup.CheckAsync(llms, invocation, Console.Error, cancellationToken) == SetupActionState.Completed;
        if (invocation.Provider.Transport == LlmTransport.OpenAiCompatibleChat)
        {
            IReadOnlyList<LlmModel> models = await llms.ListModelsAsync(invocation.Provider.ConnectionId, cancellationToken);
            bool reachable = models.Count > 0;
            Console.Error.WriteLine(reachable
                ? $"{invocation.Provider.DisplayName} is reachable and has registered models."
                : $"{invocation.Provider.DisplayName} has no registered models.");
            return reachable;
        }
        bool ready = await llms.IsAuthenticatedAsync(invocation.Provider.ConnectionId, cancellationToken);
        Console.Error.WriteLine(ready
            ? $"{invocation.Provider.DisplayName} is authenticated."
            : $"{invocation.Provider.DisplayName} is not authenticated. Run login --provider {invocation.Provider.Id}.");
        return ready;
    }

    private static Task<int> RunSetupAsync(LlmProviderService llms, ConsoleInvocation invocation, CancellationToken token)
    {
        RequireNoCommandArguments(invocation);
        return ConsoleSetup.DescribeAsync(llms, invocation, Console.Out, token);
    }

    private static async Task<int> RunAppleProbeAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, CancellationToken token)
    {
        ProviderProbeOptions options = ProviderProbeOptions.Parse(invocation.CommandArguments, invocation.Provider.Definition.DefaultModelId);
        SetupActionState readiness = await ConsoleSetup.CheckAsync(llms, invocation, Console.Error, token);
        if (readiness == SetupActionState.Failed)
        {
            Console.WriteLine("FAIL readiness: helper check failed; no generation cases run.");
            return 1;
        }
        Console.WriteLine($"Apple probe: Connect {typeof(LlmProviderService).Assembly.GetName().Version}; "
            + $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; "
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        var probe = new AppleProbe(llms.Bind(invocation.Provider.ConnectionId),
            (sessionOptions, cancellation) => agents.StartSessionAsync(invocation.Provider.ConnectionId, sessionOptions, cancellation),
            options.Model);
        return await ProbeRunner.RunAsync(probe.Cases, options, readiness == SetupActionState.Completed, Console.Out, token);
    }

    private static async Task<int> RunModelsAsync(
        LlmProviderService llms,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        RequireNoCommandArguments(invocation);
        if (!await CheckReadyAsync(llms, invocation, cancellationToken)) return 3;

        IReadOnlyList<LlmModel> models = await llms.ListModelsAsync(
            invocation.Provider.ConnectionId,
            cancellationToken);
        foreach (LlmModel model in models)
        {
            Console.WriteLine($"{model.Id}\t{model.DisplayName}");
        }

        return 0;
    }

    private static async Task<int> RunUsageAsync(
        LlmProviderService llms,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        RequireNoCommandArguments(invocation);
        LlmAccountUsageSnapshot usage = await llms.GetAccountUsageAsync(
            invocation.Provider.ConnectionId,
            cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(usage, UsageJsonOptions));
        return 0;
    }

    private static async Task<int> RunPromptAsync(
        LlmProviderService llms,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        PromptOptions options = ParsePromptOptions(invocation.CommandArguments, invocation.Provider.Definition.DefaultModelId);
        if (!await CheckReadyAsync(llms, invocation, cancellationToken)) return 3;

        string prompt = await ReadPromptAsync();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A prompt is required.");
        }

        LlmStructuredOutputRequest? structuredOutput = options.SchemaPath is null
            ? null
            : await ReadSchemaAsync(options.SchemaPath, cancellationToken);
        IReadOnlyList<LlmAttachment> attachments = await ReadAttachmentsAsync(
            options.Attachments,
            cancellationToken);
        var request = new LlmChatRequest
        {
            ModelId = options.Model,
            Messages = [new LlmChatMessage("user", prompt) { Attachments = attachments }],
            ReasoningLevel = options.Effort,
            StructuredOutput = structuredOutput
        };

        bool wroteOutput = false;
        int exitCode = 1;
        await foreach (LlmStreamEvent streamEvent in llms.StreamAsync(
                           invocation.Provider.ConnectionId,
                           request,
                           cancellationToken))
        {
            switch (streamEvent)
            {
                case LlmTextDeltaEvent text:
                    Console.Write(text.Delta);
                    wroteOutput = true;
                    break;
                case LlmStructuredOutputSnapshotEvent snapshot:
                    Console.WriteLine($"[structured snapshot] {snapshot.Content.GetRawText()}");
                    wroteOutput = true;
                    break;
                case LlmRefusalDeltaEvent refusal:
                    Console.Error.WriteLine($"[refusal] {refusal.Delta}");
                    break;
                case LlmActivityEvent activity:
                    Console.Error.WriteLine($"[activity] {activity.Activity}");
                    break;
                case LlmChatCompletionEvent completion:
                    if (wroteOutput)
                    {
                        Console.WriteLine();
                    }
                    WriteCompletion(completion.Result);
                    exitCode = completion.Result.Status is LlmCompletionStatus.Completed or LlmCompletionStatus.Unknown ? 0 : 1;
                    break;
            }
        }

        return exitCode;
    }

    private static async Task<int> RunAgentAsync(
        LlmProviderService llms,
        LlmAgentProviderService agent,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        PromptOptions options = ParsePromptOptions(invocation.CommandArguments, invocation.Provider.Definition.DefaultModelId);
        if (!await CheckReadyAsync(llms, invocation, cancellationToken)) return 3;
        LlmProviderCapabilities capabilities = await llms.GetModelCapabilitiesAsync(
            invocation.Provider.ConnectionId, options.Model, cancellationToken);
        if (!capabilities.SupportsAgentSessions)
            throw new NotSupportedException("The selected provider does not support agent sessions.");

        string prompt = await ReadPromptAsync();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A prompt is required.");
        }

        LlmStructuredOutputRequest? structuredOutput = options.SchemaPath is null
            ? null
            : await ReadSchemaAsync(options.SchemaPath, cancellationToken);
        IReadOnlyList<LlmAttachment> attachments = await ReadAttachmentsAsync(
            options.Attachments,
            cancellationToken);
        // Host tools are offered only to a runtime that can call them back.
        var tools = new List<LlmAgentToolDefinition>();
        if (capabilities.SupportsAgentToolUse)
        {
            tools.Add(CreateEchoTool());
            LlmRichContentSupported richContent = capabilities.Agent.RichContentSupported;
            if (richContent.Images == LlmRichContentCarriage.Native)
            {
                tools.Add(CreateAttachmentTool(richContent.TextDocuments != LlmRichContentCarriage.Unsupported));
            }
        }
        await using ILlmAgentSession session = await agent.StartSessionAsync(
            invocation.Provider.ConnectionId,
            new LlmAgentSessionOptions
            {
                ModelId = options.Model,
                Runtime = RuntimeFor(capabilities.Agent),
                Tools = tools,
                ToolHandler = tools.Count > 0 ? ExecuteToolAsync : null
            },
            cancellationToken);
        int exitCode = 1;
        await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                           new LlmAgentTurnRequest
                           {
                               Input = [new LlmAgentTextContent(prompt)],
                               Attachments = attachments,
                               ReasoningLevel = options.Effort,
                               OutputSchema = structuredOutput?.Schema
                           },
                           cancellationToken))
        {
            WriteAgentEvent(agentEvent);
            if (agentEvent is LlmAgentTurnCompletedEvent completed)
                exitCode = completed.Status is LlmAgentTurnStatus.Completed or LlmAgentTurnStatus.Unknown ? 0 : 1;
        }

        return exitCode;
    }

    /// <summary>
    /// Read-only host limits where the runtime enforces host policy; otherwise the provider's own configured
    /// policy, selected explicitly. Managed runtimes take no runtime options.
    /// </summary>
    private static LlmAgentNativeRuntimeOptions? RuntimeFor(LlmAgentCapabilities agent) =>
        agent.SupportedRuntimePolicies.Contains(LlmAgentRuntimePolicyKind.HostControlled)
            ? new()
            {
                WorkingDirectory = Environment.CurrentDirectory,
                Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly, ApprovalPolicy = LlmAgentApprovalPolicy.Never }
            }
            : agent.SupportedRuntimePolicies.Contains(LlmAgentRuntimePolicyKind.ProviderConfigured)
                ? new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentProviderRuntimePolicy() }
                : null;

    private static LlmAgentToolDefinition CreateEchoTool()
    {
        using JsonDocument schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string" }
              },
              "required": ["text"],
              "additionalProperties": false
            }
            """);
        return new LlmAgentToolDefinition
        {
            Name = "darkspyre_inference_connect_echo",
            Description = "Returns the supplied text. Use only when the user explicitly asks to test the echo tool.",
            InputSchema = schema.RootElement.Clone()
        };
    }

    internal static LlmAgentToolDefinition CreateAttachmentTool(bool supportsDocuments)
    {
        string schemaJson = supportsDocuments
            ?
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["image", "document"] }
              },
              "required": ["kind"],
              "additionalProperties": false
            }
            """
            :
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["image"] }
              },
              "required": ["kind"],
              "additionalProperties": false
            }
            """;
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        return new LlmAgentToolDefinition
        {
            Name = "darkspyre_inference_connect_attachment",
            Description = supportsDocuments
                ? "Returns a small image or text document attachment for Darkspyre.Inference.Connect.LLM smoke testing."
                : "Returns a small image attachment for Darkspyre.Inference.Connect.LLM smoke testing.",
            InputSchema = schema.RootElement.Clone()
        };
    }

    internal static Task<LlmToolOutput> ExecuteToolAsync(
        LlmAgentToolCall call,
        CancellationToken cancellationToken) => call.Name switch
        {
            "darkspyre_inference_connect_echo" => ExecuteEchoToolAsync(call, cancellationToken),
            "darkspyre_inference_connect_attachment" => ExecuteAttachmentToolAsync(call, cancellationToken),
            _ => Task.FromResult(LlmToolOutput.FromText($"Unknown tool '{call.Name}'.", isError: true))
        };

    private static Task<LlmToolOutput> ExecuteEchoToolAsync(
        LlmAgentToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text = call.Arguments.TryGetProperty("text", out JsonElement textElement)
            && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
        return Task.FromResult(LlmToolOutput.FromText(text));
    }

    private static Task<LlmToolOutput> ExecuteAttachmentToolAsync(
        LlmAgentToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? kind = call.Arguments.TryGetProperty("kind", out JsonElement kindElement)
            && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString()
                : null;
        LlmToolOutputBinary attachment = kind switch
        {
            "image" => new LlmToolOutputBinary(
                LlmBinaryKind.Image,
                "image/png",
                new byte[]
                {
                    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
                    0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                    0x08, 0x04, 0x00, 0x00, 0x00, 0xb5, 0x1c, 0x0c, 0x02, 0x00, 0x00, 0x00,
                    0x0b, 0x49, 0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0x64, 0xf8, 0x0f, 0x00,
                    0x01, 0x05, 0x01, 0x01, 0x27, 0x18, 0xe3, 0x66, 0x00, 0x00, 0x00, 0x00,
                    0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
                },
                "smoke-test.png"),
            "document" => new LlmToolOutputBinary(
                LlmBinaryKind.Document,
                "text/plain",
                Encoding.UTF8.GetBytes("Darkspyre.Inference.Connect.LLM attachment smoke test document."),
                "smoke-test.txt"),
            _ => throw new ArgumentException("Attachment tool kind must be 'image' or 'document'.")
        };

        return Task.FromResult(new LlmToolOutput
        {
            Parts = [new LlmToolOutputText($"Attached {attachment.FileName}."), attachment]
        });
    }

    private static void WriteAgentEvent(LlmAgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case LlmAgentTextDeltaEvent text:
                Console.Write(text.Delta);
                break;
            case LlmAgentStructuredOutputSnapshotEvent snapshot:
                Console.WriteLine($"[structured snapshot] {snapshot.Content.GetRawText()}");
                break;
            case LlmAgentReasoningDeltaEvent reasoning:
                Console.Error.Write(reasoning.Delta);
                break;
            case LlmAgentToolEvent tool:
                Console.Error.WriteLine(tool.Output is null
                    ? $"[tool:{tool.Status}] {tool.Tool}"
                    : $"[tool:{tool.Status}] {tool.Tool} output parts={tool.Output.Parts.Count} error={tool.Output.IsError}");
                break;
            case LlmAgentActivityEvent activity:
                Console.Error.WriteLine($"[activity] {activity.Activity}");
                break;
            case LlmAgentWarningEvent warning:
                Console.Error.WriteLine($"[warning] {warning.Message}");
                break;
            case LlmAgentTurnCompletedEvent completed:
                Console.WriteLine();
                Console.Error.WriteLine($"[completed] status={completed.Status}");
                break;
        }
    }

    private static PromptOptions ParsePromptOptions(string[] args, string? defaultModel = null)
    {
        string? model = defaultModel;
        LlmReasoningLevel? effort = null;
        string? schemaPath = null;
        var attachments = new List<AttachmentOption>();
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            string value = option switch
            {
                "--model" or "--effort" or "--schema" or "--attach-image" or "--attach-document"
                    when index + 1 < args.Length => args[++index],
                "--model" or "--effort" or "--schema" or "--attach-image" or "--attach-document" =>
                    throw new ArgumentException($"{option} requires a value."),
                _ => throw new ArgumentException($"Unknown prompt option '{option}'.")
            };

            switch (option)
            {
                case "--model":
                    model = value;
                    break;
                case "--effort":
                    effort = Enum.TryParse(value, ignoreCase: true, out LlmReasoningLevel level) && Enum.IsDefined(level)
                        ? level
                        : throw new ArgumentException(
                            $"--effort must be one of: {string.Join(", ", Enum.GetNames<LlmReasoningLevel>())}.");
                    break;
                case "--schema":
                    schemaPath = value;
                    break;
                case "--attach-image":
                    attachments.Add(new AttachmentOption(LlmBinaryKind.Image, value));
                    break;
                case "--attach-document":
                    attachments.Add(new AttachmentOption(LlmBinaryKind.Document, value));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("prompt requires --model <model-id>.");
        }

        return new PromptOptions(model, effort, schemaPath, attachments);
    }

    private static async Task<IReadOnlyList<LlmAttachment>> ReadAttachmentsAsync(
        IReadOnlyList<AttachmentOption> options,
        CancellationToken cancellationToken)
    {
        var attachments = new List<LlmAttachment>(options.Count);
        foreach (AttachmentOption option in options)
        {
            string path = Path.GetFullPath(option.Path);
            attachments.Add(new LlmAttachment
            {
                Kind = option.Kind,
                FileName = Path.GetFileName(path),
                Data = await File.ReadAllBytesAsync(path, cancellationToken)
            });
        }

        return attachments;
    }

    internal static BedrockLoginOptions ParseBedrockLoginOptions(string[] args)
    {
        string? profile = null;
        string? region = null;
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            string value = option switch
            {
                "--profile" or "--region" when index + 1 < args.Length => args[++index],
                "--profile" or "--region" =>
                    throw new ArgumentException($"{option} requires a value."),
                _ => throw new ArgumentException($"Unknown Bedrock login option '{option}'.")
            };

            switch (option)
            {
                case "--profile":
                    profile = value;
                    break;
                case "--region":
                    region = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(region))
        {
            throw new ArgumentException(
                "Bedrock login requires --profile <aws-profile> and --region <aws-region>.");
        }

        return new BedrockLoginOptions(profile, region);
    }

    private static async Task<string> ReadPromptAsync()
    {
        if (Console.IsInputRedirected)
        {
            return await Console.In.ReadToEndAsync();
        }

        Console.Write("Prompt: ");
        return Console.ReadLine() ?? string.Empty;
    }

    private static async Task<LlmStructuredOutputRequest> ReadSchemaAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        string name = Path.GetFileNameWithoutExtension(path);
        return new LlmStructuredOutputRequest
        {
            Name = string.IsNullOrWhiteSpace(name) ? "result" : name,
            Schema = document.RootElement.Clone()
        };
    }

    private static void WriteCompletion(LlmChatResult result)
    {
        string usage = result.Usage is null
            ? "unavailable"
            : $"{result.Usage.InputTokens ?? 0} in / {result.Usage.OutputTokens ?? 0} out";
        Console.Error.WriteLine(
            $"[completed] status={result.Status} finish={result.FinishReason ?? "unknown"} usage={usage}");
        if (result.StructuredOutput is { } structured)
        {
            Console.Error.WriteLine(
                $"[structured] enforcement={structured.Enforcement} valid={structured.IsValid}");
            foreach (string error in structured.Errors)
            {
                Console.Error.WriteLine($"[structured] {error}");
            }
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        WriteHelp();
        return 2;
    }

    private static void WriteHelp()
    {
        Console.WriteLine(
            """
            Darkspyre.Inference.Connect.ConsoleTester - direct Connect LLM smoke tests

            Usage:
              darkspyre-inference-connect-tester prompt --provider openai-compatible-api --endpoint http://gpu-box:8080/v1 --model qwen3.8-27b
              darkspyre-inference-connect-tester agent --provider openai-compatible-api --endpoint http://gpu-box:8080/v1 --model qwen3.8-27b
              darkspyre-inference-connect-tester providers
              darkspyre-inference-connect-tester setup --provider <provider-id-or-alias> [--helper <path>]
              darkspyre-inference-connect-tester status --provider apple [--helper <path>]
              darkspyre-inference-connect-tester models --provider apple [--helper <path>]
              darkspyre-inference-connect-tester vertex-probe --provider vertex --google-project <project> --google-location <location> --model <publisher/model> [--case all|text|structured|tools|agent|cancel] [--repeat 1..20] [--timeout-seconds 1..600]
              darkspyre-inference-connect-tester gemini-probe --provider gemini [--model <id>] [--case all|text|structured|tools|agent|cancel] [--repeat 1..20] [--timeout-seconds 1..600]
              darkspyre-inference-connect-tester apple-probe --provider apple [--helper <path>] [--case all|text|structured|tools|agent|cancel] [--repeat 1..20] [--timeout-seconds 1..600] [--model <id>]
              darkspyre-inference-connect-tester prompt --provider apple [--helper <path>] [--model <id>] [--schema <file>]
              darkspyre-inference-connect-tester agent --provider apple [--helper <path>] [--model <id>]
              darkspyre-inference-connect-tester login  --provider <codex|claude|antigravity>
              darkspyre-inference-connect-tester login  --provider bedrock --profile <aws-profile> --region <aws-region>
              darkspyre-inference-connect-tester login  --provider agentcore --harness-arn <arn> --profile <aws-profile> --region <aws-region>
              darkspyre-inference-connect-tester status --provider <codex|claude|antigravity|bedrock|agentcore> [--harness-arn <arn>]
              darkspyre-inference-connect-tester models --provider <codex|antigravity|bedrock>
              darkspyre-inference-connect-tester usage  --provider <codex|claude|antigravity>
              darkspyre-inference-connect-tester agy-probe --provider antigravity [--model <id>] [--case all|text|structured|native-tools|session|resume|controls|cancel] [--repeat 1..20] [--timeout-seconds 1..600]
              darkspyre-inference-connect-tester codex-probe --provider codex --model <model-id>
              darkspyre-inference-connect-tester managed-probe --provider codex --model <model-id>
              darkspyre-inference-connect-tester prompt --provider <codex|claude|antigravity|bedrock|agentcore> --model <model-id> [--harness-arn <arn>] [--effort <level>] [--schema <file>] [--attach-image <file>] [--attach-document <file>]
              darkspyre-inference-connect-tester agent  --provider <codex|claude|agentcore|antigravity> --model <model-id> [--harness-arn <arn>] [--effort <level>] [--schema <file>] [--attach-image <file>] [--attach-document <file>]

            prompt reads one line interactively, or the complete prompt from redirected standard input:
              echo 'Reply only with OK.' | darkspyre-inference-connect-tester prompt --provider codex --model gpt-5.6-terra
              echo 'Reply only with OK.' | darkspyre-inference-connect-tester prompt --provider antigravity --model auto
              echo 'Reply only with OK.' | darkspyre-inference-connect-tester prompt --provider bedrock --model amazon.nova-micro-v1:0
              echo 'Hello' | darkspyre-inference-connect-tester agent --provider codex --model gpt-5.6-terra
              echo 'Call darkspyre_inference_connect_echo with text Hello.' | darkspyre-inference-connect-tester agent --provider agentcore --harness-arn ARN --model default
              echo 'Summarize the attachment.' | darkspyre-inference-connect-tester agent --provider claude --model sonnet --attach-document ./report.pdf
              echo 'Test the echo tool with hello' | darkspyre-inference-connect-tester agent --provider claude --model sonnet
              echo 'Call darkspyre_inference_connect_attachment with kind image and confirm you received it.' | darkspyre-inference-connect-tester agent --provider codex --model gpt-5.6-terra
              echo 'Call darkspyre_inference_connect_attachment with kind document and quote its text.' | darkspyre-inference-connect-tester agent --provider claude --model sonnet

            providers and setup display adapter-owned metadata without generation or authentication.
            OpenAI-compatible --endpoint selects an anonymous test endpoint without reading or writing stored credentials.
            Apple needs its separately built helper and a ready on-device model; no login is required.
            Apple defaults its model from provider metadata. --helper accepts a name on PATH or an absolute path.
            vertex-probe performs explicit metered cloud generation using ADC; missing access is reported as skipped.
            gemini-probe performs explicit metered Gemini API generation with the key that login --provider gemini stored from GEMINI_API_KEY.
            apple-probe performs explicit local generation, reports each case and never retries a failed case.
            agy-probe runs explicit subscription turns through the Antigravity native session in a temporary
            workspace, reports the CLI version and turn count, and never retries a failed case.
            agent offers host tools only to runtimes that support them, and selects the provider's configured
            runtime policy for Antigravity, whose own settings decide its permissions.
            Exit codes: 0 passed, 1 failed, 3 skipped prerequisites, 130 cancelled (invalid invocation: 2).

            Codex login uses ChatGPT device OAuth, stored in Darkspyre.SecureStore for direct requests.
            Claude and Antigravity login verify the official CLI's existing login. Their CLI
            credentials remain owned by the official CLI and are not copied into Darkspyre.SecureStore.
            Bedrock and AgentCore login store only the AWS profile name and region; the AWS SDK resolves
            credentials through its standard credential chain. AgentCore commands also require the
            deployed Harness ARN because the console tester does not own a connection database.
            Codex, Claude, and AgentCore agent modes expose darkspyre_inference_connect_echo. Codex and Claude also
            expose darkspyre_inference_connect_attachment.
            Codex and Claude support image and document prompt attachments and tool-result fixtures.
            Antigravity headless input is text-only and rejects attachment blocks.
            """);
    }

    private static void RequireNoCommandArguments(ConsoleInvocation invocation)
    {
        if (invocation.CommandArguments.Length > 0)
        {
            throw new ArgumentException(
                $"{invocation.Command} does not accept '{invocation.CommandArguments[0]}'.");
        }
    }

    private sealed record PromptOptions(
        string Model,
        LlmReasoningLevel? Effort,
        string? SchemaPath,
        IReadOnlyList<AttachmentOption> Attachments);

    private sealed record AttachmentOption(LlmBinaryKind Kind, string Path);

    internal sealed record BedrockLoginOptions(string Profile, string Region);
}
