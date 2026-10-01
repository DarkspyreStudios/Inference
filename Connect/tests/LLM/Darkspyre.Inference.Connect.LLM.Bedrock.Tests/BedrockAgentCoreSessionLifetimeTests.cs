using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAgentCoreSessionLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetirementCancelsAndDrainsHeldSdkOpeningOrRead(bool read, bool ignore)
    {
        var invoker = new HeldInvoker(read, ignore);
        ILlmAgentSession session = await SessionAsync(invoker);
        var events = new List<LlmAgentEvent>();
        Task turn = ReadAsync(session.RunTurnAsync(Turn), events);
        await invoker.Started.Task.WaitAsync(Bound);
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            await invoker.Stopped.Task.WaitAsync(Bound);
            Assert.True(invoker.Token.IsCancellationRequested);
            Assert.False(invoker.Disposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
            invoker.Release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.True(invoker.Disposed);
            Assert.DoesNotContain(events.OfType<LlmAgentTextDeltaEvent>(), item => item.Delta == "late");
            Assert.Empty(events.OfType<LlmAgentTurnCompletedEvent>());
            Assert.Equal(1, invoker.Calls);
        }
        finally
        {
            invoker.Release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementCancelsAndDrainsHeldHostToolWithoutLateReplyOrAnotherInvocation(bool ignore)
    {
        var started = Completion();
        var stopped = Completion();
        var release = Completion();
        CancellationToken toolToken = default;
        var invoker = new ToolInvoker();
        async Task<LlmToolOutput> Tool(LlmAgentToolCall call, CancellationToken token)
        {
            toolToken = token;
            using CancellationTokenRegistration registration = token.Register(() => stopped.TrySetResult());
            started.TrySetResult();
            try { await release.Task.WaitAsync(ignore ? CancellationToken.None : token); }
            finally { await release.Task; }
            return LlmToolOutput.FromText("late tool result");
        }
        ILlmAgentSession session = await SessionAsync(invoker, Tool);
        var events = new List<LlmAgentEvent>();
        Task turn = ReadAsync(session.RunTurnAsync(Turn), events);
        await started.Task.WaitAsync(Bound);
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            await stopped.Task.WaitAsync(Bound);
            Assert.True(toolToken.IsCancellationRequested);
            release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.Equal(1, invoker.Calls);
            Assert.Single(events.OfType<LlmAgentToolEvent>());
            Assert.Null(Assert.Single(events.OfType<LlmAgentToolEvent>()).Output);
            Assert.Empty(events.OfType<LlmAgentTurnCompletedEvent>());
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APausedIteratorMustExitBeforeRetirementAndCannotResumeEffects(bool resume)
    {
        var invoker = new ToolInvoker();
        ILlmAgentSession session = await SessionAsync(invoker);
        IAsyncEnumerator<LlmAgentEvent> reader = session.RunTurnAsync(Turn).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            if (resume) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
            await reader.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.Equal(0, invoker.Calls);
        }
        finally
        {
            await Record.ExceptionAsync(async () => await reader.DisposeAsync());
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly LlmAgentTurnRequest Turn = new() { Input = [new LlmAgentTextContent("question")] };

    private static Task<ILlmAgentSession> SessionAsync(IBedrockAgentCoreInvoker invoker, LlmAgentToolHandler? tool = null) =>
        new BedrockAgentCoreLlmClient(invoker).StartSessionAsync("connection", new()
        {
            Version = "v1", Transport = LlmTransport.AwsBedrockAgentCore,
            Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test-harness",
            Credential = new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "controlled" } }.Serialize()
        }, new()
        {
            ModelId = "default",
            Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly, ApprovalPolicy = LlmAgentApprovalPolicy.Never } },
            Tools = tool is null ? [] : [new() { Name = "lookup", Description = "Controlled lookup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }],
            ToolHandler = tool
        });

    private static async Task ReadAsync(IAsyncEnumerable<LlmAgentEvent> stream, List<LlmAgentEvent> events)
    { await foreach (LlmAgentEvent item in stream) events.Add(item); }

    private sealed class HeldInvoker(bool read, bool ignore) : IBedrockAgentCoreInvoker
    {
        internal TaskCompletionSource Started { get; } = Completion();
        internal TaskCompletionSource Stopped { get; } = Completion();
        internal TaskCompletionSource Release { get; } = Completion();
        internal CancellationToken Token { get; private set; }
        internal bool Disposed { get; private set; }
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Stopped.TrySetResult());
            try
            {
                if (read) yield return new HarnessContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "initial" } };
                Started.TrySetResult();
                await Release.Task.WaitAsync(ignore ? CancellationToken.None : cancellationToken);
                yield return new HarnessContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "late" } };
                yield return new HarnessMessageStopEvent { StopReason = HarnessStopReason.End_turn };
            }
            finally { await Release.Task; Disposed = true; }
        }
    }

    private sealed class ToolInvoker : IBedrockAgentCoreInvoker
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new HarnessContentBlockStartEvent { ContentBlockIndex = 0, Start = new() { ToolUse = new() { ToolUseId = "call", Name = "lookup", Type = HarnessToolUseType.Tool_use } } };
            yield return new HarnessMessageStopEvent { StopReason = HarnessStopReason.Tool_use };
        }
    }
}
