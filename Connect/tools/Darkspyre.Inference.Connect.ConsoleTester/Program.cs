using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Bedrock;
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

        ConsoleInvocation invocation;
        try
        {
            invocation = ConsoleInvocation.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            WriteHelp();
            return 2;
        }

        var services = new ServiceCollection();
        services.AddSecureStore(options =>
        {
            options.ServiceName = CredentialServiceName;
            options.Label = "Darkspyre.Inference.Connect.LLM Console Tester";
        });
        services.AddSingleton(invocation);
        services.AddSingleton<SecureConnectionSource>();
        services.AddSingleton<ILlmConnectionSource>(provider =>
            provider.GetRequiredService<SecureConnectionSource>());
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        await using ServiceProvider provider = services.BuildServiceProvider();
        LlmProviderService llms = provider.GetRequiredService<LlmProviderService>();
        LlmAgentProviderService agent = provider.GetRequiredService<LlmAgentProviderService>();

        try
        {
            return invocation.Command switch
            {
                "login" => await RunLoginAsync(llms, provider.GetRequiredService<ILlmConnectionSource>(), invocation, cancellation.Token),
                "status" => await RunStatusAsync(llms, invocation, cancellation.Token),
                "models" => await RunModelsAsync(llms, invocation, cancellation.Token),
                "usage" => await RunUsageAsync(llms, invocation, cancellation.Token),
                "prompt" => await RunPromptAsync(llms, invocation, cancellation.Token),
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
        catch (LlmProviderException exception)
        {
            Console.Error.WriteLine($"Provider error [{exception.ProviderCode ?? "unknown"}]: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or FormatException
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
        bool ready = await llms.IsAuthenticatedAsync(
            invocation.Provider.ConnectionId,
            cancellationToken);
        Console.WriteLine(ready
            ? $"{invocation.Provider.DisplayName} is authenticated."
            : $"{invocation.Provider.DisplayName} is not authenticated. Run login --provider {invocation.Provider.Id}.");
        return ready ? 0 : 3;
    }

    private static async Task<int> RunModelsAsync(
        LlmProviderService llms,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        RequireNoCommandArguments(invocation);
        if (!await llms.IsAuthenticatedAsync(
                invocation.Provider.ConnectionId,
                cancellationToken))
        {
            Console.Error.WriteLine(
                $"{invocation.Provider.DisplayName} is not authenticated. Run login --provider {invocation.Provider.Id}.");
            return 3;
        }

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
        PromptOptions options = ParsePromptOptions(invocation.CommandArguments);
        if (!await llms.IsAuthenticatedAsync(
                invocation.Provider.ConnectionId,
                cancellationToken))
        {
            Console.Error.WriteLine(
                $"{invocation.Provider.DisplayName} is not authenticated. Run login --provider {invocation.Provider.Id}.");
            return 3;
        }

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
                    break;
            }
        }

        return 0;
    }

    private static async Task<int> RunAgentAsync(
        LlmProviderService llms,
        LlmAgentProviderService agent,
        ConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Provider.Transport is not (
                LlmTransport.OpenAiCodex
                or LlmTransport.AnthropicClaudeCli
                or LlmTransport.AwsBedrockAgentCore))
        {
            throw new NotSupportedException(
                "Agent mode is available only for Codex, Claude, and AgentCore connections.");
        }

        PromptOptions options = ParsePromptOptions(invocation.CommandArguments);
        if (!await llms.IsAuthenticatedAsync(invocation.Provider.ConnectionId, cancellationToken))
        {
            Console.Error.WriteLine(
                $"{invocation.Provider.DisplayName} is not authenticated. "
                + $"Run login --provider {invocation.Provider.Id}.");
            return 3;
        }

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
        LlmProviderCapabilities capabilities = await llms.GetModelCapabilitiesAsync(
            invocation.Provider.ConnectionId,
            options.Model,
            cancellationToken);
        var tools = new List<LlmAgentToolDefinition> { CreateEchoTool() };
        LlmRichContentSupported richContent = capabilities.Agent.RichContentSupported;
        if (richContent.Images == LlmRichContentCarriage.Native)
        {
            tools.Add(CreateAttachmentTool(richContent.TextDocuments != LlmRichContentCarriage.Unsupported));
        }
        await using ILlmAgentSession session = await agent.StartSessionAsync(
            invocation.Provider.ConnectionId,
            new LlmAgentSessionOptions
            {
                ModelId = options.Model,
                Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Sandbox = LlmAgentSandboxMode.ReadOnly, ApprovalPolicy = LlmAgentApprovalPolicy.Never },
                Tools = tools,
                ToolHandler = ExecuteToolAsync
            },
            cancellationToken);
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
        }

        return 0;
    }

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
            case LlmAgentReasoningDeltaEvent reasoning:
                Console.Error.Write(reasoning.Delta);
                break;
            case LlmAgentToolEvent tool:
                Console.Error.WriteLine($"[tool:{tool.Status}] {tool.Tool}");
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

    private static PromptOptions ParsePromptOptions(string[] args)
    {
        string? model = null;
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
            Darkspyre.Inference.Connect.ConsoleTester - direct smoke tests for Darkspyre.Inference.Connect.LLM subscription providers

            Usage:
              darkspyre-inference-connect-tester login  --provider <codex|claude|antigravity>
              darkspyre-inference-connect-tester login  --provider bedrock --profile <aws-profile> --region <aws-region>
              darkspyre-inference-connect-tester login  --provider agentcore --harness-arn <arn> --profile <aws-profile> --region <aws-region>
              darkspyre-inference-connect-tester status --provider <codex|claude|antigravity|bedrock|agentcore> [--harness-arn <arn>]
              darkspyre-inference-connect-tester models --provider <codex|antigravity|bedrock>
              darkspyre-inference-connect-tester usage  --provider <codex|claude|antigravity>
              darkspyre-inference-connect-tester codex-probe --provider codex --model <model-id>
              darkspyre-inference-connect-tester managed-probe --provider codex --model <model-id>
              darkspyre-inference-connect-tester prompt --provider <codex|claude|antigravity|bedrock|agentcore> --model <model-id> [--harness-arn <arn>] [--effort <level>] [--schema <file>] [--attach-image <file>] [--attach-document <file>]
              darkspyre-inference-connect-tester agent  --provider <codex|claude|agentcore> --model <model-id> [--harness-arn <arn>] [--effort <level>] [--schema <file>] [--attach-image <file>] [--attach-document <file>]

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
