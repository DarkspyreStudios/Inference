using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAgentCoreUsageLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RetirementRejectsIgnoringLateMetadataAndDrainsItsSdkEnumerator()
    {
        var invoker = new HeldInvoker();
        ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", Connection(), Options());
        IAsyncEnumerator<LlmAgentEvent> reader = session.RunTurnAsync(Turn).GetAsyncEnumerator();
        Task<bool>? pending = null;
        try
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(new LlmUsage(4, 1, 5), Assert.IsType<LlmAgentUsageEvent>(reader.Current).Usage);
            pending = reader.MoveNextAsync().AsTask();
            await invoker.Waiting.Task.WaitAsync(Bound);
            Task retirement = session.DisposeAsync().AsTask();
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            Assert.True(invoker.Token.IsCancellationRequested);
            invoker.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
            await reader.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.True(invoker.Disposed);
        }
        finally
        {
            invoker.Release.TrySetResult();
            if (pending is not null) await Record.ExceptionAsync(() => pending.WaitAsync(Bound));
            await reader.DisposeAsync();
            await session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("native")]
    [InlineData("retired")]
    public void ActualUsageCallersReleaseTheirCollectibleInferenceAndSdkGeneration(string caller)
    {
        (WeakReference generation, WeakReference[] roots) = ExerciseGeneration(caller);
        for (int attempt = 0; generation.IsAlive && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive, "A settled usage/session/SDK graph remains rooted."));
        Assert.False(generation.IsAlive, "The actual usage callers retain their collectible Inference/SDK generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Generation, WeakReference[] Roots) ExerciseGeneration(string caller)
    {
        string directory = Path.GetDirectoryName(typeof(BedrockAgentCoreUsageLifetimeTests).Assembly.Location)!;
        var generation = new Generation(directory);
        var weak = new WeakReference(generation);
        WeakReference[] roots;
        try
        {
            foreach (Assembly original in new[] { typeof(BedrockAgentCoreUsageLifetimeTests).Assembly,
                typeof(BedrockAgentCoreSession).Assembly, typeof(LlmProviderService).Assembly,
                typeof(LlmUsage).Assembly, typeof(HarnessMetadataEvent).Assembly, typeof(IEventStreamEvent).Assembly })
            {
                Assembly owned = generation.LoadFromAssemblyPath(original.Location);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(owned));
                Assert.Equal(original.ManifestModule.ModuleVersionId, owned.ManifestModule.ModuleVersionId);
            }
            Type fixture = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(BedrockAgentCoreUsageLifetimeTests).Assembly.GetName().Name)
                .GetType(typeof(BedrockAgentCoreUsageLifetimeTests).FullName!, true)!;
            var task = (Task<WeakReference[]>)fixture.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [caller])!;
            roots = task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return (weak, roots);
    }

    private static async Task<WeakReference[]> ForeignCallerAsync(string caller)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(BedrockAgentCoreSession).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(HarnessMetadataEvent).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(IEventStreamEvent).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmUsage).Assembly));
        if (caller == "retired")
        {
            await new BedrockAgentCoreUsageLifetimeTests().RetirementRejectsIgnoringLateMetadataAndDrainsItsSdkEnumerator();
            return [];
        }
        var invoker = new HeldInvoker();
        invoker.Release.TrySetResult();
        var roots = new List<WeakReference> { new(invoker) };
        if (caller == "chat")
        {
            LlmChatResult? result = null;
            await foreach (LlmStreamEvent item in Client(invoker).StreamAsync(Connection(), new() { ModelId = "default", Messages = [new("user", "hello")] }))
            {
                roots.Add(new(item));
                if (item is LlmUsageDeltaEvent delta) Assert.Same(generation, AssemblyLoadContext.GetLoadContext(delta.Cumulative.GetType().Assembly));
                if (item is LlmChatCompletionEvent completion) result = completion.Result;
            }
            Assert.Equal(new LlmUsage(4, 2, 6), result!.Usage);
        }
        else
        {
            ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", Connection(), Options());
            roots.Add(new(session));
            await foreach (LlmAgentEvent item in session.RunTurnAsync(Turn))
            {
                roots.Add(new(item));
                if (item is LlmAgentUsageEvent { Usage: { } usage })
                    Assert.Same(generation, AssemblyLoadContext.GetLoadContext(usage.GetType().Assembly));
            }
            Task retirement = session.DisposeAsync().AsTask();
            roots.Add(new(retirement));
            await retirement;
        }
        Assert.True(invoker.Disposed);
        roots.Add(new(invoker.First));
        return [.. roots];
    }

    private static BedrockAgentCoreLlmClient Client(HeldInvoker invoker) => new(invoker);
    private static LlmAgentTurnRequest Turn => new() { Input = [new LlmAgentTextContent("hello")] };
    private static LlmAgentSessionOptions Options() => new()
    { ModelId = "default", Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentHostRuntimePolicy() } };
    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockAgentCore,
        Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test-harness",
        Credential = new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "test-profile" } }.Serialize()
    };
    private sealed class HeldInvoker : IBedrockAgentCoreInvoker
    {
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HarnessMetadataEvent First { get; } = new() { Usage = new() { InputTokens = 4, OutputTokens = 1, TotalTokens = 5 } };
        internal bool Disposed { get; private set; }
        internal CancellationToken Token { get; private set; }
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            try
            {
                yield return First;
                Waiting.TrySetResult();
                await Release.Task;
                yield return new HarnessMetadataEvent { Usage = new() { InputTokens = 4, OutputTokens = 2, TotalTokens = 6 } };
                yield return new HarnessMessageStopEvent { StopReason = HarnessStopReason.End_turn };
            }
            finally { Disposed = true; }
        }
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true
                && name.Name?.StartsWith("AWSSDK.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
