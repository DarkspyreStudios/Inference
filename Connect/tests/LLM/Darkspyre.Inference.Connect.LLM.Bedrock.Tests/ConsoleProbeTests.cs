using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.ConsoleTester;
using Microsoft.Extensions.DependencyInjection;
using SecureStore;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class ConsoleProbeTests
{
    [Fact]
    public async Task CatalogAndSetupUseRegisteredMetadataWithoutGenerating()
    {
        using var container = Container();
        var clients = container.GetServices<ILlmClient>().ToArray();
        var catalog = new ConsoleTesterProviders(clients);
        Assert.Equal(clients.Length, catalog.All.Count);
        foreach (var client in clients)
        {
            var entry = catalog.Resolve(client.ProviderDefinition.Id);
            Assert.Same(client.ProviderDefinition, entry.Definition);
            Assert.Equal(client.ProviderDefinition.DefaultEndpoint, entry.Endpoint);
        }
        var apple = catalog.Resolve("apple");
        Assert.Same(apple, catalog.Resolve("apple-foundation-models"));
        var invocation = ConsoleInvocation.Parse(["setup", "--provider", "apple", "--helper", "/missing/helper"], catalog);
        var source = new SecureConnectionSource(new InMemoryCredentialStore(), catalog, invocation);
        var connection = Assert.IsType<LlmConnection>(await source.GetConnectionAsync(apple.ConnectionId));
        Assert.Equal("/missing/helper", connection.Endpoint);
        Assert.Null(connection.Credential);
        var service = new LlmProviderService(source, clients);
        using var output = new StringWriter();
        Assert.Equal(0, await ConsoleSetup.DescribeAsync(service, invocation, output, CancellationToken.None));
        Assert.Contains(apple.Definition.SetupSteps[0].Instructions, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Check helper and on-device model", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PotentialBilling", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-key:", output.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<NotSupportedException>(() => source.SaveCredentialAsync(apple.ConnectionId, "secret", connection.Version));
    }

    [Fact]
    public void AppleInvocationRejectsWrongProviderAndRelativeHelperPaths()
    {
        using var container = Container();
        var catalog = new ConsoleTesterProviders(container.GetServices<ILlmClient>());
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(["apple-probe", "--provider", "codex"], catalog));
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(["status", "--provider", "codex", "--helper", "/helper"], catalog));
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(["status", "--provider", "apple", "--helper", "./helper"], catalog));
        Assert.Throws<ArgumentException>(() => ConsoleInvocation.Parse(["status", "--provider", "apple", "--helper", "a", "--helper", "b"], catalog));
        Assert.Equal("apple", ConsoleInvocation.Parse(["status", "--provider", "apple"], catalog).Provider.Id);
        Assert.Equal("agentcore", ConsoleInvocation.Parse(["setup", "--provider", "agentcore"], catalog).Provider.Id);
    }

    [Theory]
    [InlineData("--repeat", "0")]
    [InlineData("--repeat", "21")]
    [InlineData("--timeout-seconds", "601")]
    [InlineData("--timeout-seconds", "no")]
    public void ProbeRejectsUnboundedOrUnknownOptions(string option, string value) =>
        Assert.Throws<ArgumentException>(() => ProviderProbeOptions.Parse([option, value], "apple-system-default"));

    [Fact]
    public async Task ProbeCaseNamesComeFromTheSuppliedCases()
    {
        var test = new ProbeCase("native-tools", _ => Task.FromResult("done"));
        using var output = new StringWriter();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            ProbeRunner.RunAsync([test], new("model", "missing", 1, 1), true, output, CancellationToken.None));

        Assert.Contains("native-tools", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ProbeRunner.RunAsync([test], new("model", "native-tools", 1, 1), true, output, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatsRetainFailuresAndOmitProviderErrorText()
    {
        int called = 0;
        var test = new ProbeCase("text", _ => ++called == 1
            ? throw new IOException("secret-provider-response") : Task.FromResult("done"));
        using var output = new StringWriter();
        int exit = await ProbeRunner.RunAsync([test], new("model", "all", 2, 1), true, output, CancellationToken.None);
        Assert.Equal(1, exit);
        Assert.Equal(2, called);
        Assert.Contains("FAIL 1/2", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("PASS 2/2", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("passed=1 failed=1 skipped=0", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-response", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPrerequisitesSkipWithoutCallingTheModel()
    {
        using var output = new StringWriter();
        int exit = await ProbeRunner.RunAsync([new("text", _ => throw new InvalidOperationException())],
            new("model", "all", 2, 1), false, output, CancellationToken.None);
        Assert.Equal(3, exit);
        Assert.Contains("passed=0 failed=0 skipped=2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeadlineFailsAndCallerCancellationStopsRemainingCases()
    {
        var pending = new ProbeCase("text", async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        });
        using var output = new StringWriter();
        Assert.Equal(1, await ProbeRunner.RunAsync([pending], new("model", "all", 1, 1), true, output, CancellationToken.None));
        Assert.Contains("case deadline exceeded", output.ToString(), StringComparison.Ordinal);
        using var cancellation = new CancellationTokenSource();
        int called = 0;
        var cancel = new ProbeCase("text", token =>
        {
            called++;
            cancellation.Cancel();
            return Task.FromCanceled<string>(token);
        });
        Assert.Equal(130, await ProbeRunner.RunAsync([cancel, pending], new("model", "all", 2, 1), true, output, cancellation.Token));
        Assert.Equal(1, called);
        Assert.Contains("CANCELLED", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuredCaseRequiresSnapshotsEvenWhenFinalJsonIsValid(bool snapshots)
    {
        var inference = new StubInference((_, token) => Events(
            snapshots ? [new LlmStructuredOutputSnapshotEvent(JsonElement.Parse("{}")), new LlmCompletionEvent(Result() with
            { StructuredOutput = new(LlmStructuredOutputEnforcement.ProviderJsonSchema, true, true, []) })]
            : [new LlmCompletionEvent(Result() with { StructuredOutput = new(LlmStructuredOutputEnforcement.ProviderJsonSchema, true, true, []) })], token));
        var test = Probe(inference).Cases.Single(test => test.Name == "structured");
        if (snapshots) Assert.Contains("final schema valid", await test.Run(CancellationToken.None), StringComparison.Ordinal);
        else await Assert.ThrowsAsync<ProbeFailure>(() => test.Run(CancellationToken.None));
    }

    [Fact]
    public async Task ToolCaseUsesDistinctIdsAndReplaysReversedResultsWithToolsDisabled()
    {
        int requests = 0;
        var inference = new StubInference((request, token) =>
        {
            if (++requests == 1)
                return Events([new LlmCompletionEvent(Result() with
                {
                    Status = LlmCompletionStatus.RequiresAction,
                    ToolCalls = [new LlmToolCall("one", "lookup", """{"key":"sample"}"""), new LlmToolCall("two", "lookup", """{"key":"sample"}""")]
                })], token);
            Assert.Equal(LlmToolChoiceMode.None, request.ToolChoice?.Mode);
            Assert.Equal(["two", "one"], request.Messages[^1].ToolResults.Select(result => result.ToolCallId));
            return Events([new LlmCompletionEvent(Result())], token);
        });
        await Probe(inference).Cases.Single(test => test.Name == "tools").Run(CancellationToken.None);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task TextCaseRejectsStreamThatDoesNotMatchItsTerminal()
    {
        var inference = new StubInference((_, token) => Events(
            [new LlmTextDeltaEvent("different"), new LlmCompletionEvent(Result())], token));
        await Assert.ThrowsAsync<ProbeFailure>(() => Probe(inference).Cases.Single(test => test.Name == "text").Run(CancellationToken.None));
    }

    [Fact]
    public async Task ToolCaseRejectsDuplicateCallIdsBeforeReplay()
    {
        int requests = 0;
        var inference = new StubInference((_, token) =>
        {
            requests++;
            return Events([new LlmCompletionEvent(Result() with
            {
                Status = LlmCompletionStatus.RequiresAction,
                ToolCalls = [new LlmToolCall("same", "lookup", """{"key":"sample"}"""), new LlmToolCall("same", "lookup", """{"key":"sample"}""")]
            })], token);
        });
        await Assert.ThrowsAsync<ProbeFailure>(() => Probe(inference).Cases.Single(test => test.Name == "tools").Run(CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancellationCaseWaitsForCancellationThenStartsANewOperation()
    {
        int requests = 0;
        bool disposed = false;
        var inference = new StubInference((_, token) => ++requests == 1 ? CancelStream(token) : Next(token));
        async IAsyncEnumerable<LlmStreamEvent> CancelStream([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return new LlmTextDeltaEvent("hello");
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { disposed = true; }
        }
        IAsyncEnumerable<LlmStreamEvent> Next(CancellationToken token)
        {
            Assert.True(disposed);
            return Events([new LlmCompletionEvent(Result())], token);
        }
        await Probe(inference).Cases.Single(test => test.Name == "cancel").Run(CancellationToken.None);
        Assert.Equal(2, requests);
    }

    private static ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        return services.BuildServiceProvider();
    }

    private static AppleProbe Probe(ILlmInference inference) => new(inference, (_, _) => throw new InvalidOperationException(), "model");
    private static LlmInferenceResult Result() => new()
    {
        Text = "hello",
        Status = LlmCompletionStatus.Unknown,
        Provenance = new() { ImplementationId = "test", ModelId = "model" }
    };

    private static async IAsyncEnumerable<LlmStreamEvent> Events(LlmStreamEvent[] events,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var item in events)
        {
            token.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private sealed class StubInference(Func<LlmInferenceRequest, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream) : ILlmInference
    {
        public InferenceImplementationDescriptor Descriptor => new("test", "Test", InferenceImplementationKind.Connect, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default) => stream(request, cancellationToken);
    }
}
