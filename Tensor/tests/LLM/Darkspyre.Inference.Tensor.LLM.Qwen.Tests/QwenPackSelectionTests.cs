using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;
using TensorSharp.Server;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenPackSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-qwen-selection-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Weights = "controlled checkpoint bytes"u8.ToArray();

    [Fact]
    public async Task SamplingControlsReachTheEngineAndStopsWorkWithGreedyDecoding()
    {
        var runtime = new Runtime();
        var (engine, packs, model) = await LoadedAsync(runtime, "host/sampling");
        await using (engine)
        using (packs)
        {
            await Collect(engine, Request(model) with
            { Temperature = .6, TopP = .8, TopK = 17, Seed = 42, PresencePenalty = .2, FrequencyPenalty = -.3, StopSequences = ["stop"] });
            SamplingConfig sampling = Assert.IsType<SamplingConfig>(runtime.LastSampling);
            Assert.Equal(.6f, sampling.Temperature);
            Assert.Equal(.8f, sampling.TopP);
            Assert.Equal(17, sampling.TopK);
            Assert.Equal(42, sampling.Seed);
            Assert.Equal(.2f, sampling.PresencePenalty);
            Assert.Equal(-.3f, sampling.FrequencyPenalty);
            Assert.Equal(["stop"], sampling.StopSequences);
            await Collect(engine, Request(model) with { StopSequences = ["end"], PresencePenalty = 3 });
            Assert.True(runtime.LastSampling!.Temperature <= 0);
            Assert.Equal(["end"], runtime.LastSampling.StopSequences);
            Assert.Equal(3f, runtime.LastSampling.PresencePenalty);
            var capabilities = await engine.GetCapabilitiesAsync(model);
            Assert.True(capabilities.SupportsTopP && capabilities.SupportsTopK && capabilities.SupportsSeed);
            Assert.True(capabilities.SupportsPresencePenalty && capabilities.SupportsFrequencyPenalty);
            Assert.Equal(16, capabilities.MaxStopSequences);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public async Task GreedySamplingRejectsTopPTopKAndSeedBeforeTheEngine(double? temperature)
    {
        var runtime = new Runtime();
        var (engine, packs, model) = await LoadedAsync(runtime, "host/greedy");
        await using (engine)
        using (packs)
        {
            foreach (var request in new[] { Request(model) with { TopP = .8 }, Request(model) with { TopK = 17 }, Request(model) with { Seed = 42 } })
            {
                var error = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, request with { Temperature = temperature }));
                Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
            }
            Assert.Null(runtime.LastSampling);
        }
    }

    [Fact]
    public async Task InvalidSamplingFailsBeforeAnUnloadedModelIsResolved()
    {
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, Pack("host/invalid"));
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        foreach (var request in new[]
        {
            Request("host/invalid") with { Temperature = double.NaN },
            Request("host/invalid") with { Temperature = 1, TopP = 0 },
            Request("host/invalid") with { Temperature = 1, TopP = double.Epsilon },
            Request("host/invalid") with { Temperature = 1, TopK = 0 },
            Request("host/invalid") with { Temperature = 1, Seed = -1 },
            Request("host/invalid") with { PresencePenalty = double.MaxValue },
            Request("host/invalid") with { FrequencyPenalty = double.NaN },
            Request("host/invalid") with { StopSequences = [""] }
        })
        {
            var error = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, request));
            Assert.Equal(InferenceFailureCode.InvalidRequest, error.Code);
        }
        Assert.Null(runtime.LastSampling);
    }

    [Fact]
    public async Task SelectedPackOwnsProvenanceAndLimitsOnlyAppearFromLoadedMetadata()
    {
        TensorModelPackDefinition first = Pack("host/first"), second = Pack("host/second");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, first, second);
        await packs.InstallAsync(first.Key);
        await packs.InstallAsync(second.Key);
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        Assert.All(await engine.ListModelsAsync(), model => Assert.Null(model.MaxContextTokens));
        foreach (TensorModelPackDefinition pack in new[] { first, second })
        {
            await engine.LoadAndWaitAsync(pack.Key);
            Assert.Equal(pack.Key, (await engine.GetObservationAsync()).Model);
            Assert.Equal(8192, (await engine.ListModelsAsync()).Single(model => model.Id == pack.Key.ModelId).MaxContextTokens);
            LlmCompletionEvent completion = Assert.IsType<LlmCompletionEvent>((await Collect(engine, pack.Key.ModelId))[^1]);
            Assert.Equal(pack.Key.ModelId, completion.Result.Provenance!.ModelId);
            Assert.Equal(pack.Key.Revision, completion.Result.Provenance.ModelRevision);
            Assert.Equal("hello", completion.Result.Text);
            InferenceException wrong = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, "another/model"));
            Assert.Equal(InferenceFailureCode.ModelUnavailable, wrong.Code);
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(pack.Key));
            await engine.UnloadAndWaitAsync();
            Assert.Null((await engine.GetObservationAsync()).Model);
            Assert.Null(runtime.ContextLength);
            await packs.RemoveAsync(pack.Key);
        }
        Assert.Equal(new[] { first.Key, second.Key }, runtime.Keys);
    }

    [Fact]
    public async Task ReferencedInputsFailBeforeAnUnloadedModelOrNativeRuntimeIsEntered()
    {
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, Pack("host/references"));
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        LlmMessage[] messages =
        [
            new("user", "inspect") { Attachments = [new LlmFileReference(new("native"))] },
            new("user", "inspect") { Attachments = [new LlmResourceAttachment(new("workspace", "original"))] },
            new("user", "inspect") { ToolResults = [new LlmToolResult("call", new() { Parts = [new LlmToolOutputSessionResource(new("workspace", "generated"))] })] }
        ];
        foreach (var message in messages)
        {
            var error = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine,
                Request("host/references") with { Messages = [message] }));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        }
        Assert.Empty(runtime.Keys);
        Assert.Null(runtime.LastSampling);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShellDefinitionsAndHistoryRefuseBeforeModelOrNativeRuntimeEntry(int variant)
    {
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, Pack("host/shell"));
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        LlmShellCall call = new("actual-shell", ["printf observed"], 0, 0);
        LlmInferenceRequest request = variant switch
        {
            0 => Request("host/shell") with { Tools = [new LlmShellTool([])] },
            1 => Request("host/shell") with { Messages = [new("assistant", "") { ToolCalls = [call] }] },
            _ => Request("host/shell") with
            {
                Messages = [new("assistant", "") { ToolCalls = [call] }, new("user", "")
                { ToolResults = [new LlmShellCallResult(call.Id, [new("", null, new LlmShellExitedOutcome(0))])] }]
            }
        };
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, request));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Empty(runtime.Keys);
        Assert.Null(runtime.LastSampling);
    }

    [Fact]
    public async Task SetupSavesCustomSelectionWithoutInstalling()
    {
        TensorModelPackDefinition pack = Pack("host/custom");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        var target = new Target();
        TensorSettingsManager manager = QwenSetup.CreateSettingsManager(target, packs, offeredModels: [pack]);
        TensorSettings setup = await manager.OpenAsync(new(SetupChangeIntent.Create, "custom", null));
        var field = Assert.IsType<SetupChoiceField>(setup.Steps[0].Sections[0].Fields[0]);
        Assert.Equal(SetupEditState.Applied, field.SetValue(pack.Key.ToString()).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(setup)).State);
        Assert.Equal(pack.Key, target.Changes!.Configuration.Model);
        var editTarget = new Target();
        TensorSettingsManager editManager = QwenSetup.CreateSettingsManager(editTarget, packs, offeredModels: [pack]);
        TensorSettings edit = await editManager.OpenAsync(new(SetupChangeIntent.Edit, "custom", "saved-v1"), target.Changes.Configuration);
        SetupChoiceField backend = Assert.IsType<SetupChoiceField>(edit.Steps.SelectMany(step => step.Sections)
            .SelectMany(section => section.Fields).Single(item => item.Description.Id == "backend"));
        Assert.Equal(SetupEditState.Applied, backend.SetValue(nameof(TensorBackendKind.Cpu)).State);
        Assert.Equal(SetupCommitState.Committed, (await editManager.SaveAsync(edit)).State);
        Assert.Equal("saved-v1", editTarget.Changes!.Identity.ExpectedVersion);
        Assert.Equal(pack.Key, editTarget.Changes.Configuration.Model);
        Assert.Equal(TensorBackendKind.Cpu, editTarget.Changes.Configuration.Backend);
        Assert.True(editTarget.Changes.RequiresUnload);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(pack.Key)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledLoadClearsRuntimeAndLease(bool cancel)
    {
        TensorModelPackDefinition pack = Pack("host/failure");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { OnLoad = () => { if (cancel) { cancellation.Cancel(); } else { throw new InvalidDataException(); } } };
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAndWaitAsync(pack.Key, cancellation.Token)); }
        else { Assert.Equal(InferenceFailureCode.ModelCorrupt, (await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(pack.Key))).Code); }
        Assert.Null(runtime.ContextLength);
        Assert.Null((await engine.GetObservationAsync()).Model);
        await packs.RemoveAsync(pack.Key);
    }

    [Theory]
    [InlineData(TensorBackendKind.Cpu)]
    [InlineData(TensorBackendKind.GgmlCpu)]
    public async Task FailedLoadCleanupRetainsItsPackAndRefusesReplacementUntilReleaseSucceeds(TensorBackendKind backend)
    {
        TensorModelPackDefinition first = Pack("host/held-failure"), second = Pack("host/replacement");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, first, second);
        await packs.InstallAsync(first.Key);
        await packs.InstallAsync(second.Key);
        var original = new InvalidDataException("controlled load failure");
        var cleanup = new InvalidOperationException("controlled unload failure");
        bool failCleanup = true;
        var runtime = new Runtime
        {
            OnLoad = () => throw original,
            OnUnload = () => { if (failCleanup) throw cleanup; }
        };
        GgmlRuntimeOwner owner = GgmlRuntimeOwnerTests.CreateControlledCpuOwner(out var supplier);
        var engine = new QwenLlmInference(packs, runtime, backend, runtimeOwner: owner);
        try
        {
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => engine.LoadAndWaitAsync(first.Key));
            Assert.Same(original, error.InnerExceptions[0]);
            Assert.Same(cleanup, error.InnerExceptions[1]);
            await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(second.Key));
            Assert.Equal(new[] { first.Key }, runtime.Keys);
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(first.Key));
            Assert.Equal(0, supplier.ModelReleases);
            if (backend == TensorBackendKind.GgmlCpu)
                Assert.Equal(1, (await owner.ShutdownAsync()).OutstandingLeases);

            failCleanup = false;
            await engine.UnloadAndWaitAsync();
            await packs.RemoveAsync(first.Key);
            await packs.RemoveAsync(second.Key);
            Assert.Null(runtime.ContextLength);
            Assert.Equal(backend == TensorBackendKind.GgmlCpu ? 1 : 0, supplier.ModelReleases);
        }
        finally
        {
            failCleanup = false;
            await engine.DisposeAsync();
            await owner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(TensorBackendKind.Cpu, false)]
    [InlineData(TensorBackendKind.Cpu, true)]
    [InlineData(TensorBackendKind.GgmlCpu, false)]
    [InlineData(TensorBackendKind.GgmlCpu, true)]
    public async Task FailedStopAndRepeatedDisposalExposeFailureUntilExplicitReleaseOnlyRetry(
        TensorBackendKind backend, bool cleanupAvailableBeforeDisposal)
    {
        TensorModelPackDefinition pack = Pack("host/failed-stop");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var cleanup = new InvalidOperationException("controlled retirement failure");
        bool failCleanup = true;
        var runtime = new Runtime { OnUnload = () => { if (failCleanup) throw cleanup; } };
        GgmlRuntimeOwner owner = GgmlRuntimeOwnerTests.CreateControlledCpuOwner(out var supplier);
        var engine = new QwenLlmInference(packs, runtime, backend, runtimeOwner: owner);
        Task? disposal = null;
        try
        {
            await engine.LoadAndWaitAsync(pack.Key);
            Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StopAndWaitAsync()));
            int failedAttempts = runtime.UnloadAttempts;
            if (cleanupAvailableBeforeDisposal) failCleanup = false;
            disposal = engine.DisposeAsync().AsTask();
            Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => disposal));
            Assert.Equal(failedAttempts, runtime.UnloadAttempts);
            Assert.Same(disposal, engine.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(pack.Key));
            Assert.Equal(0, runtime.Disposals);
            Assert.Equal(0, supplier.ModelReleases);
            if (backend == TensorBackendKind.GgmlCpu)
                Assert.Equal(1, (await owner.ShutdownAsync()).OutstandingLeases);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadAsync(pack.Key));

            failCleanup = false;
            await engine.StopAndWaitAsync();
            await packs.RemoveAsync(pack.Key);
            Assert.Equal(1, runtime.Disposals);
            Assert.Equal(backend == TensorBackendKind.GgmlCpu ? 1 : 0, supplier.ModelReleases);
            Assert.Equal(TensorEngineState.Stopped, (await engine.GetObservationAsync()).State);
            Assert.Same(disposal, engine.DisposeAsync().AsTask());
        }
        finally
        {
            failCleanup = false;
            if ((await engine.GetObservationAsync()).State != TensorEngineState.Stopped)
                await engine.StopAndWaitAsync();
            if (disposal is not null) { try { await disposal; } catch (InvalidOperationException) { } }
            else { try { await engine.DisposeAsync(); } catch (InvalidOperationException) { } }
            await owner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RacingStopAndDisposalShareOneActualCleanupAttempt(bool failFirst)
    {
        TensorModelPackDefinition pack = Pack("host/racing-stop");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new InvalidOperationException("controlled held cleanup failure");
        int unloads = 0;
        var runtime = new Runtime
        {
            OnUnload = () =>
            {
                if (Interlocked.Increment(ref unloads) != 1) return;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                if (failFirst) throw cleanup;
            }
        };
        var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        Task? disposal = null;
        try
        {
            await engine.LoadAndWaitAsync(pack.Key);
            await engine.StopAsync();
            await entered.Task;
            await engine.StopAsync();
            disposal = engine.DisposeAsync().AsTask();
            Assert.Same(disposal, engine.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref unloads));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadAsync(pack.Key));
            release.TrySetResult();
            if (failFirst)
            {
                Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => disposal));
                Assert.Equal(0, runtime.Disposals);
                await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(pack.Key));
                await engine.StopAndWaitAsync();
            }
            else await disposal;
            Assert.Equal(1, runtime.Disposals);
            await packs.RemoveAsync(pack.Key);
        }
        finally
        {
            disposal ??= engine.DisposeAsync().AsTask();
            release.TrySetResult();
            try { await disposal; } catch (InvalidOperationException) { }
            if ((await engine.GetObservationAsync()).State != TensorEngineState.Stopped)
                await engine.StopAndWaitAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingLeaseProtectsNativeLifetimeDuringUnload(bool urgent)
    {
        TensorModelPackDefinition pack = Pack("host/stream");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        await using IAsyncEnumerator<LlmStreamEvent> stream = engine.StreamAsync(Request(pack.Key.ModelId)).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Task unloading = engine.UnloadAndWaitAsync(new CancellationToken(urgent));
        Assert.False(unloading.IsCompleted);
        Assert.NotNull(runtime.ContextLength);
        if (urgent) { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await stream.MoveNextAsync(); }); }
        else { while (await stream.MoveNextAsync()) { } }
        await stream.DisposeAsync();
        await unloading;
        Assert.Null(runtime.ContextLength);
    }

    [Fact]
    public async Task DisposingAnUnfinishedStreamAbortsItsEngineRequest()
    {
        TensorModelPackDefinition pack = Pack("host/abandon");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var runtime = new Runtime();
        await using var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);

        await using (IAsyncEnumerator<LlmStreamEvent> stream = engine.StreamAsync(Request(pack.Key.ModelId)).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
        }
        Assert.True(runtime.LastStreamToken.IsCancellationRequested);

        await Collect(engine, pack.Key.ModelId);
        Assert.False(runtime.LastStreamToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ThinkingStreamsAsReasoningApartFromTheAnswer()
    {
        var runtime = new Runtime { Architecture = "qwen35", Pieces = ["Checking the question.", "</think>\n\n", "Hi there"] };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/thinking");
        await using (engine)
        using (packs)
        {
            List<LlmStreamEvent> events = await Collect(engine, Request(model) with { ReasoningLevel = LlmReasoningLevel.High });
            Assert.True(runtime.LastThinking);
            Assert.True((await engine.GetCapabilitiesAsync(model)).SupportsReasoningText);
            Assert.True((await engine.GetCapabilitiesAsync(model)).SupportsStreamingUsage);
            Assert.Contains("Checking the question.", string.Concat(events.OfType<LlmReasoningDeltaEvent>().Select(item => item.Delta)), StringComparison.Ordinal);
            Assert.Equal("Hi there", string.Concat(events.OfType<LlmTextDeltaEvent>().Select(item => item.Delta)).Trim());
            Assert.Equal("Hi there", Assert.IsType<LlmCompletionEvent>(events[^1]).Result.Text.Trim());
            Assert.NotNull(Assert.IsType<LlmCompletionEvent>(events[^1]).Result.Usage);
        }
    }

    [Fact]
    public async Task ForeignContinuationRequiresExplicitOmissionAndReportsIt()
    {
        var runtime = new Runtime { Architecture = "qwen35", Pieces = ["done"] };
        var (engine, packs, model) = await LoadedAsync(runtime, "host/continuation");
        await using (engine)
        using (packs)
        {
            var state = new LlmContinuationState
            { ImplementationId = "foreign", ModelId = model, ScopeId = "scope", Payload = "private" };
            var request = Request(model) with { Messages = [new("assistant", "prior") { ContinuationState = state }, new("user", "continue")] };
            var rejected = await Assert.ThrowsAsync<InferenceException>(() => Collect(engine, request));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, rejected.Code);
            var events = await Collect(engine, request with { UnboundContinuationState = LlmUnboundContinuationState.Omit });
            Assert.Equal(1, Assert.IsType<LlmCompletionEvent>(events[^1]).Result.OmittedContinuationStateCount);
            Assert.Same(state, request.Messages[0].ContinuationState);
            Assert.Equal(LlmStrictInputSchemaSupport.Unsupported, (await engine.GetCapabilitiesAsync(model)).Tools.StrictInputSchema);
        }
    }

    [Fact]
    public async Task ToolCallsStreamAsCommonEventsAndTheirResultsReturnAsToolMessages()
    {
        var runtime = new Runtime
        {
            Architecture = "qwen35",
            Pieces = ["<tool_call>\n{\"name\": \"read\", \"arguments\": {\"path\": \"a.txt\"}}\n</tool_call>"]
        };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/tools");
        await using (engine)
        using (packs)
        {
            using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");
            LlmToolDefinition read = new() { Name = "read", Description = "Reads a file.", InputSchema = schema.RootElement.Clone() };
            List<LlmStreamEvent> events = await Collect(engine, Request(model) with { Tools = [read] });

            Assert.Equal("read", Assert.Single(runtime.LastTools!).Name);
            LlmToolCallStartedEvent started = Assert.Single(events.OfType<LlmToolCallStartedEvent>());
            Assert.Equal("read", started.Name);
            LlmToolCall call = Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall;
            Assert.Equal(started.ToolCallId, call.Id);
            Assert.Equal("a.txt", JsonElement.Parse(call.ArgumentsJson).GetProperty("path").GetString());
            Assert.Equal(call, Assert.Single(Assert.IsType<LlmCompletionEvent>(events[^1]).Result.ToolCalls));

            await Collect(engine, Request(model) with
            {
                Tools = [read],
                Messages =
                [
                    new("user", "Read a.txt"),
                    new("assistant", "") { ToolCalls = [call] },
                    new("user", "") { ToolResults = [new LlmToolResult(call.Id, LlmToolOutput.FromText("contents"))] }
                ]
            });
            ChatMessage assistant = runtime.LastMessages.Single(message => message.Role == "assistant");
            Assert.Equal("read", Assert.Single(assistant.ToolCalls!).Name);
            ChatMessage result = runtime.LastMessages.Single(message => message.Role == "tool");
            Assert.Equal(call.Id, result.ToolCallId);
            Assert.Equal("contents", result.Content);
            Assert.DoesNotContain(runtime.LastMessages, message => message.Role == "user" && message.Content == "");
        }
    }

    [Fact]
    public async Task ReasoningLevelsAreNoneAndHighAndToolControlsAreRefused()
    {
        var runtime = new Runtime();
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/levels");
        await using (engine)
        using (packs)
        {
            LlmCapabilities capabilities = await engine.GetCapabilitiesAsync(model);
            Assert.Equal(new HashSet<LlmReasoningLevel> { LlmReasoningLevel.None, LlmReasoningLevel.High }, capabilities.ReasoningLevels.ToHashSet());
            Assert.True(capabilities.SupportsToolUse);
            InferenceException level = await Assert.ThrowsAsync<InferenceException>(() =>
                Collect(engine, Request(model) with { ReasoningLevel = LlmReasoningLevel.Medium }));
            Assert.Equal(InferenceFailureCode.InvalidRequest, level.Code);
            InferenceException choice = await Assert.ThrowsAsync<InferenceException>(() =>
                Collect(engine, Request(model) with { AllowParallelToolCalls = false }));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, choice.Code);
        }
    }

    [Fact]
    public async Task ARequestLargerThanTheFittedContextFailsBeforeTheEngineRuns()
    {
        var runtime = new Runtime { LoadedContext = 4096, PromptEstimate = 3500 };
        (QwenLlmInference engine, TensorModelPackManager packs, string model) = await LoadedAsync(runtime, "host/fit");
        await using (engine)
        using (packs)
        {
            Assert.Equal(4096, (await engine.ListModelsAsync()).Single().MaxContextTokens);
            InferenceException full = await Assert.ThrowsAsync<InferenceException>(() =>
                Collect(engine, Request(model) with { MaxOutputTokens = 1000 }));
            Assert.Equal(InferenceFailureCode.CapacityExhausted, full.Code);
            Assert.Empty(runtime.LastMessages);
            List<LlmStreamEvent> events = await Collect(engine, Request(model) with { MaxOutputTokens = 500 });
            Assert.IsType<LlmCompletionEvent>(events[^1]);
        }
    }

    private async Task<(QwenLlmInference Engine, TensorModelPackManager Packs, string Model)> LoadedAsync(Runtime runtime, string id)
    {
        TensorModelPackDefinition pack = Pack(id);
        var http = new HttpClient(new Handler());
        TensorModelPackManager packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var engine = new QwenLlmInference(packs, runtime, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        return (engine, packs, pack.Key.ModelId);
    }

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) => new(new(definitions), _root, http);
    private static TensorModelPackDefinition Pack(string id) => TensorLlmModels.Qwen3FourBillionQ4Km with
    {
        Key = new(id, "custom-revision"),
        Artifacts = [new() { Role = "model", RelativePath = "model.gguf", Length = Weights.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Weights)), Sources = [new("https://weights.example.test/model.gguf")] }]
    };
    private static LlmInferenceRequest Request(string id) => new() { ModelId = id, Messages = [new("user", "hello")] };
    private static Task<List<LlmStreamEvent>> Collect(QwenLlmInference engine, string id) => Collect(engine, Request(id));
    private static async Task<List<LlmStreamEvent>> Collect(QwenLlmInference engine, LlmInferenceRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in engine.StreamAsync(request)) { events.Add(item); }
        return events;
    }
    private sealed class Runtime : IQwenModelRuntime
    {
        public List<TensorModelKey> Keys { get; } = [];
        public string? Architecture { get; init; } = "qwen3";
        public IReadOnlyList<string> Pieces { get; init; } = ["hello"];
        public List<ToolFunction>? LastTools { get; private set; }
        public bool LastThinking { get; private set; }
        public SamplingConfig? LastSampling { get; private set; }
        public List<ChatMessage> LastMessages { get; private set; } = [];
        public int LoadedContext { get; init; } = 8192;
        public int? PromptEstimate { get; init; }
        public int? EstimatePromptTokens(List<ChatMessage> messages, List<ToolFunction>? tools, bool enableThinking) => PromptEstimate;
        public Action? OnLoad { get; init; }
        public Action? OnUnload { get; init; }
        public int Disposals { get; private set; }
        public int UnloadAttempts { get; private set; }
        public int? ContextLength { get; private set; }
        public string? LoadedBackend { get; private set; }
        public CancellationToken LastStreamToken { get; private set; }
        public Task LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, string backend, CancellationToken cancellationToken)
        {
            Assert.Equal(definition.Key, lease.Key);
            Keys.Add(lease.Key); ContextLength = LoadedContext; LoadedBackend = backend;
            OnLoad?.Invoke();
            return Task.CompletedTask;
        }
        public void Unload() { UnloadAttempts++; OnUnload?.Invoke(); ContextLength = null; LoadedBackend = null; }
        public void ApplyGrammar(SamplingConfig sampling, StructuredOutputFormat format, bool enableThinking) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatSession session, List<ChatMessage> messages, int maxTokens,
            SamplingConfig sampling, List<ToolFunction>? tools, bool enableThinking, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastStreamToken = cancellationToken;
            LastTools = tools;
            LastThinking = enableThinking;
            LastSampling = sampling;
            LastMessages = messages;
            await Task.Yield();
            foreach (string piece in Pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ChatStreamUpdate.Text(piece);
            }
            cancellationToken.ThrowIfCancellationRequested();
            yield return new("", true, 1, 1, 0, 0, 0, 0, "stop");
        }
        public void Dispose() { Unload(); Disposals++; }
    }
    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "version")); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Weights) });
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
