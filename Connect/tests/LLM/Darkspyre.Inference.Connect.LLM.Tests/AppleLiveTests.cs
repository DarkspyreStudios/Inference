using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

// Optional synthetic on-device experiment. The normal suite reports an explicit skip; it never
// generates content, downloads a model, changes settings or loads FoundationModels by default.
public sealed class AppleLiveTests(ITestOutputHelper output)
{
    [AppleLiveFact]
    public async Task RegisteredBindSupportsTextSchemaToolsAndSemanticReplay()
    {
        using ServiceProvider container = Container();
        using IServiceScope scope = container.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        Assert.Contains(service.SupportedProviders, p => p.Id == "apple-foundation-models");
        var model = Assert.Single(await service.ListModelsAsync("apple"));
        output.WriteLine($"Model: {model.Id}, context capacity: {model.MaxContextTokens}");
        var bound = service.Bind("apple");
        var text = await bound.CompleteAsync(new() { ModelId = model.Id, Messages = [new("user", "Reply with the word hello.")], MaxOutputTokens = 32 });
        Assert.Equal(LlmCompletionStatus.Unknown, text.Status);
        Assert.NotEmpty(text.Text);
        Assert.Null(text.Usage);
        output.WriteLine($"Text status: {text.Status}; characters: {text.Text.Length}");

        var structured = await AppleFoundationModelsTests.Read(bound.StreamAsync(new()
        {
            ModelId = model.Id,
            Messages = [new("user", "Set key to the word sample.")],
            MaxOutputTokens = 128,
            StructuredOutput = new() { Name = "sample", Schema = AppleFoundationModelsTests.Json(AppleFoundationModelsTests.ObjectSchema) }
        }));
        Assert.NotEmpty(structured.OfType<LlmStructuredOutputSnapshotEvent>());
        Assert.Empty(structured.OfType<LlmTextDeltaEvent>());
        var result = Assert.Single(structured.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Unknown, result.Status);
        Assert.True(result.StructuredOutput?.IsValid);
        output.WriteLine($"Structured snapshots: {structured.OfType<LlmStructuredOutputSnapshotEvent>().Count()}; final valid: {result.StructuredOutput?.IsValid}");

        LlmMessage prompt = new("user", "Use lookup twice, both times with key sample, to obtain two independent readings. Do not invent the readings.");
        var toolRequest = new LlmInferenceRequest { ModelId = model.Id, Messages = [prompt], Tools = [AppleFoundationModelsTests.Tool], MaxOutputTokens = 256 };
        var calls = await bound.CompleteAsync(toolRequest);
        Assert.Equal(LlmCompletionStatus.RequiresAction, calls.Status);
        Assert.NotEmpty(calls.ToolCalls);
        Assert.Equal(calls.ToolCalls.Count, calls.ToolCalls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        output.WriteLine($"Tool batch: {calls.ToolCalls.Count}; distinct IDs: true");
        var replay = await bound.CompleteAsync(toolRequest with
        {
            ToolChoice = new() { Mode = LlmToolChoiceMode.None },
            Messages = [prompt, new("assistant", calls.Text) { ToolCalls = calls.ToolCalls },
                new("user", "Report the readings already returned. Do not call any tools.")
                { ToolResults = calls.ToolCalls.Reverse().Select((c, i) => new LlmToolResult(c.Id, LlmToolOutput.FromText($"Synthetic reading {i + 1}: seven"))).ToArray() }]
        });
        Assert.Equal(LlmCompletionStatus.Unknown, replay.Status);
        Assert.Empty(replay.ToolCalls);
        Assert.NotEmpty(replay.Text);
        output.WriteLine($"Replay with reversed results and no active tools: {replay.Status}, {replay.Text.Length} characters");
    }

    [AppleLiveFact]
    public async Task ManagedAgentExecutesHostToolAndKeepsSettledContext()
    {
        using ServiceProvider container = Container();
        using IServiceScope scope = container.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<LlmAgentProviderService>();
        int executed = 0;
        var tool = AppleFoundationModelsTests.Tool;
        await using var session = await agents.StartSessionAsync("apple", new()
        {
            ModelId = "apple-system-default",
            Instructions = "Use lookup to retrieve requested readings. After a lookup result is present, report it without looking it up again.",
            Tools = [new() { Name = tool.Name, Description = tool.Description, InputSchema = tool.InputSchema }],
            ToolHandler = (_, _) => { Interlocked.Increment(ref executed); return Task.FromResult(LlmToolOutput.FromText("Synthetic reading: seven")); }
        });
        var events = await AppleFoundationModelsTests.Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Look up key sample and report its reading.")] }));
        var done = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        output.WriteLine($"Agent terminal: {done.Status}, reason: {done.Reason}, host executions: {executed}");
        Assert.Equal(LlmAgentTurnStatus.Unknown, done.Status);
        Assert.True(executed > 0);
        var context = await session.CaptureContextAsync();
        Assert.Equal(executed, context.Entries.Sum(e => e.Message.ToolResults.Count));
        Assert.NotEmpty(context.Entries[^1].Message.Content);
        output.WriteLine($"Managed agent: {executed} host executions, {context.Entries.Count} retained entries, terminal {done.Reason}");
    }

    [AppleLiveFact]
    public async Task CancellationDuringNativeStreamingSettlesThenNextRequestWorks()
    {
        using ServiceProvider container = Container();
        using IServiceScope scope = container.CreateScope();
        var bound = scope.ServiceProvider.GetRequiredService<LlmProviderService>().Bind("apple");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int chunks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in bound.StreamAsync(new() { ModelId = "apple-system-default", Messages = [new("user", "Write a long story about a lighthouse.")] }, cancellation.Token))
                if (item is LlmTextDeltaEvent) { chunks++; cancellation.Cancel(); }
        });
        Assert.True(chunks > 0);
        var next = await bound.CompleteAsync(new() { ModelId = "apple-system-default", Messages = [new("user", "Reply with OK.")], MaxOutputTokens = 16 });
        Assert.Equal(LlmCompletionStatus.Unknown, next.Status);
        output.WriteLine($"Native stream cancelled after {chunks} text chunks; next operation: {next.Status}");
    }

    private ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource, Source>();
        services.AddLlmConnect();
        services.AddSingleton<IAppleProcessRunner>(new TraceRunner(output));
        return services.BuildServiceProvider();
    }
    private sealed class TraceRunner(ITestOutputHelper log) : IAppleProcessRunner
    {
        public async IAsyncEnumerable<JsonElement> RunAsync(string executable, AppleCommand command, AppleProcessLimits limits,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var stream = new AppleProcessRunner().RunAsync(executable, command, limits, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                try { next = await stream.MoveNextAsync(); }
                catch (InferenceException error) { log.WriteLine($"Process failure: {error.Code}"); throw; }
                if (!next) break;
                var frame = stream.Current;
                string type = frame.GetProperty("type").GetString()!;
                if (type == "terminal")
                    log.WriteLine($"{command.Command} terminal: {frame.GetProperty("status").GetString()}, code: {(frame.TryGetProperty("code", out var code) ? code.GetString() : "none")}");
                yield return frame;
            }
        }
    }
    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(AppleFoundationModelsTests.Connection with { Endpoint = Environment.GetEnvironmentVariable("APPLE_FM_HELPER")! });
        public Task SaveCredentialAsync(string id, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

internal sealed class AppleLiveFactAttribute : FactAttribute
{
    public AppleLiveFactAttribute()
    {
        if (!MacFactAttribute.Supported) Skip = "Live Apple generation requires macOS 26 or later on Apple Silicon.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLE_FM_HELPER")))
            Skip = "Opt in to on-device generation with APPLE_FM_HELPER set to the built helper's absolute path.";
    }
}
