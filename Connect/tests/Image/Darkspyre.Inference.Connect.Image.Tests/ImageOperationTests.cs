using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageOperationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("success")]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("commit")]
    [InlineData("commit-cleanup")]
    [InlineData("selection")]
    public async Task ResourceOutputPublicationRetainsActualBytesAndFailureFacts(string outcome)
    {
        var transport = new ImageTestTransport();
        var resources = new ImageTestResources { Outcome = outcome };
        var progress = new ProgressRecorder();
        IImageInference inference = new ImageProviderService([transport]).Bind(Connection(), Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        try
        {
            if (outcome == "success")
            {
                ImageGenerationResult result = await inference.GenerateAsync(Request(), progress);
                Assert.Equal(2, result.Images.Count);
                Assert.Equal("image/webp", result.Images[0].ContentType);
                Assert.Null(result.Images[1].ContentType);
                Assert.Equal("native-0", result.Images[0].File!.FileId);
                Assert.All(result.Images, image => Assert.NotNull(image.Resource));
                Assert.Equal(2, resources.Writers.Count);
                Assert.All(resources.Writers, writer =>
                {
                    Assert.Equal(new byte[] { 7, 8 }, writer.Bytes.ToArray());
                    Assert.Equal(1, writer.Commits);
                    Assert.True(writer.Selected);
                    Assert.Equal(1, writer.Disposals);
                });
                Assert.Equal(ImageGenerationStage.Completed, progress.Stages[^1]);
                Assert.Equal(0, resources.SeparateSelectionCalls);
            }
            else
            {
                Exception error = await Assert.ThrowsAnyAsync<Exception>(() => inference.GenerateAsync(Request(), progress));
                InferenceResourceOperationException operation = outcome == "commit-cleanup"
                    ? Assert.IsType<InferenceResourceOperationException>(Assert.IsType<AggregateException>(error).InnerExceptions[0])
                    : Assert.IsType<InferenceResourceOperationException>(error);
                Assert.DoesNotContain(ImageGenerationStage.Completed, progress.Stages);
                Assert.Equal(outcome switch { "open" => InferenceResourceOperationPhase.OpenWrite, "write" => InferenceResourceOperationPhase.Write, _ => InferenceResourceOperationPhase.Commit }, operation.Failure.Phase);
                if (outcome == "open") Assert.Null(operation.Failure.Reference);
                else Assert.Equal(resources.Writers[0].Reference, operation.Failure.Reference);
                if (outcome == "write") Assert.Null(operation.Failure.BytesTransferred);
                else Assert.Equal(outcome == "open" ? 0 : 2, operation.Failure.BytesTransferred);
                Assert.Equal(outcome switch
                { "open" or "write" => InferenceResourceCommitOutcome.NotAttempted, "selection" => InferenceResourceCommitOutcome.Committed, _ => InferenceResourceCommitOutcome.Unknown }, operation.Failure.CommitOutcome);
                if (outcome is "commit" or "commit-cleanup" or "selection")
                    Assert.Equal(InferenceResourceCommitOutcome.Unknown, operation.Failure.SelectionOutcome);
                if (outcome == "commit-cleanup")
                    Assert.Same(resources.CleanupFailure, Assert.IsType<AggregateException>(error).InnerExceptions[1]);
                if (outcome != "selection") Assert.Same(resources.OperationFailure, operation.InnerException);
                if (resources.Writers.Count > 0) Assert.Equal(1, resources.Writers[0].Disposals);
            }
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
        Assert.Equal(0, resources.Disposals);
        Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task RetirementDrainsIgnoringCommitPreservesCommittedEffectsAndPublishesNoResult()
    {
        var resources = new ImageTestResources { CommitRelease = new() };
        var transport = new ImageTestTransport();
        IImageInference inference = new ImageProviderService([transport]).Bind(Connection(), Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        Task<ImageGenerationResult> pending = inference.GenerateAsync(Request());
        await resources.CommitEntered.Task.WaitAsync(Bound);
        Task retirement = ((IAsyncDisposable)inference).DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inference.GetCapabilitiesAsync());
        resources.CommitRelease.SetResult();
        InferenceResourceOperationCanceledException error = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => pending.WaitAsync(Bound));
        await retirement.WaitAsync(Bound);
        Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.CommitOutcome);
        Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.SelectionOutcome);
        Assert.Equal(new InferenceResourceSelectionVersion("workspace", "session", 1), error.Failure.SelectionVersion);
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, resources.Writers[0].Disposals);
        Assert.True(resources.Writers[0].Selected);
        Assert.Equal(1, transport.Cancellations);
        Assert.Equal(0, resources.Disposals);
        Assert.Equal(0, transport.Disposals);
        await ((IAsyncDisposable)inference).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteWithoutOutputIsProtocolFailure(bool empty)
    {
        var transport = new ImageTestTransport { Outputs = empty ? [] : null };
        IImageInference inference = new ImageProviderService([transport]).Bind(Connection(), Workflow());
        try
        {
            InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => inference.GenerateAsync(Request()));
            Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
            Assert.Equal(0, transport.Downloads);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Fact]
    public async Task AuthoritativeCommitRefusalRetainsItsExactTypedFactsAndIdentity()
    {
        var resources = new ImageTestResources { Outcome = "commit-refused" };
        IImageInference inference = new ImageProviderService([new ImageTestTransport()]).Bind(Connection(), Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        try
        {
            InferenceResourceOperationException failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.GenerateAsync(Request()));
            Assert.Same(resources.AuthoritativeCommitFailure, failure);
            Assert.Equal(InferenceResourceCommitOutcome.NotCommitted, failure.Failure.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, failure.Failure.SelectionOutcome);
            Assert.Equal(1, resources.Writers[0].Disposals);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Fact]
    public async Task CleanupFailureAfterSuccessfulCommitPreservesCommittedSelectionFacts()
    {
        var resources = new ImageTestResources { Outcome = "cleanup" };
        IImageInference inference = new ImageProviderService([new ImageTestTransport()]).Bind(Connection(), Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        try
        {
            InferenceResourceOperationException failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.GenerateAsync(Request()));
            Assert.Same(resources.CleanupFailure, failure.InnerException);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, failure.Failure.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, failure.Failure.SelectionOutcome);
            Assert.Equal(new InferenceResourceSelectionVersion("workspace", "session", 1), failure.Failure.SelectionVersion);
            Assert.Equal(2, failure.Failure.BytesTransferred);
            Assert.Equal(1, resources.Writers[0].Disposals);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Fact]
    public async Task XaiCompositeUsesOneSnapshotThroughEveryNestedCallAndForwardsControls()
    {
        var source = new XaiSource();
        await using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("chosen"), access);
        var transport = new ImageTestTransport { Kind = ImageTransport.XaiImagine, ChangedSource = source };
        var headers = new Dictionary<string, string> { ["X-Test"] = "initial" };
        transport.CallerHeaders = headers;
        ImageConnection connection = new() { Transport = ImageTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = binding.Selection, Authorization = binding.Authorization, AdditionalHeaders = headers };
        IImageInference inference = new ImageProviderService([transport]).Bind(connection, Workflow() with { Transport = ImageTransport.XaiImagine }, binding, pollInterval: TimeSpan.Zero);
        try
        {
            ImageGenerationResult result = await inference.GenerateAsync(Request() with
            { Count = 2, Steps = 3, GuidanceScale = 1.5, References = [new BinaryMediaSource(null, new byte[] { 1 })] });
            Assert.Equal(2, result.Images.Count);
            Assert.Equal(2, transport.Prepared!.Count);
            Assert.Equal(3, transport.Prepared.Steps);
            Assert.Equal(1.5, transport.Prepared.GuidanceScale);
            Assert.Null(transport.Prepared.Seed);
            Assert.Equal(1, source.Reads);
            Assert.Equal(0, source.Saves);
            Assert.NotEmpty(transport.Bindings);
            Assert.All(transport.Bindings, entered => Assert.Same(transport.Bindings[0], entered));
            Assert.All(transport.HeaderValues, value => Assert.Equal("initial", value));
            Assert.All(transport.Versions, value => Assert.Equal("v1", value));
            Assert.Equal("changed", headers["X-Test"]);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
        Assert.Equal(0, source.Disposals);
        Assert.Equal(0, transport.Disposals);
    }

    internal static ImageConnection Connection() => new() { Transport = ImageTransport.ComfyUi, Endpoint = "http://provider.invalid" };
    internal static ImageWorkflow Workflow() => new() { Id = "workflow", Label = "Workflow", Transport = ImageTransport.ComfyUi, TransportConfiguration = JsonElement.Parse("{}") };
    internal static ImageGenerationRequest Request() => new() { ModelId = "workflow", Prompt = "draw" };

    private sealed class ProgressRecorder : IProgress<ImageGenerationProgress>
    {
        public List<ImageGenerationStage> Stages { get; } = [];
        public void Report(ImageGenerationProgress value) => Stages.Add(value.Stage);
    }
}

internal sealed class ImageTestTransport : IImageTransport, IDisposable
{
    public ImageTransport Kind { get; init; } = ImageTransport.ComfyUi;
    public XaiSource? ChangedSource { get; init; }
    public Dictionary<string, string>? CallerHeaders { get; set; }
    public ImageTransportPreparationRequest? Prepared { get; private set; }
    public int Downloads { get; private set; }
    public int Cancellations { get; private set; }
    public int Disposals { get; private set; }
    public List<XaiFamilyBinding> Bindings { get; } = [];
    public List<string> Versions { get; } = [];
    public List<string> HeaderValues { get; } = [];
    public IReadOnlyList<ImageTransportOutput>? Outputs { get; init; } =
    [new("first.png", new("owner", "identity"), JsonElement.Parse("{}"), File: new("native-0")), new(null, new("owner", "identity"), JsonElement.Parse("{}"))];
    public ImageTransportDescriptor Descriptor => new(Kind, "Controlled Image transport", "Managed fixture; no target qualification", new(false, true, true));
    public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public async Task<ImageCapabilities> GetCapabilitiesAsync(ImageConnection connection, ImageWorkflow workflow, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        await ObserveAsync(connection, xai);
        if (ChangedSource is not null) ChangedSource.Current = ChangedSource.Current with { Version = "v2", Endpoint = "https://us.api.x.ai/v1", Xai = new() { Region = XaiRegion.UnitedStates } };
        if (CallerHeaders is not null) CallerHeaders["X-Test"] = "changed";
        return new() { SupportedSizes = [new(1024, 1024)], MaximumImageCount = 2, SupportsSeed = true, SupportsNegativePrompt = true,
            SupportsSteps = true, SupportsGuidanceScale = true, MaximumReferences = 1, SupportedReferenceKinds = new[] { InferenceMediaSourceKind.Binary }.ToFrozenSet() };
    }
    public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) { Prepared = request; return new("{}"); }
    public Task<ImageProviderHealth> TestAsync(ImageConnection connection, ImageWorkflow? workflow = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult<IImageTransportProgressConnection?>(null);
    public Task<ImagePreparedReference> UploadReferenceAsync(ImageConnection connection, ImageReference reference, Stream content, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async Task<ImagePreparedReference> ResolveReferenceAsync(ImageConnection connection, InferenceMediaSource source, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    { await ObserveAsync(connection, xai); return new(new() { Id = Guid.NewGuid() }, new("owner", "identity"), JsonElement.Parse("{}")); }
    public async Task<string> SubmitAsync(ImageConnection connection, PreparedImageTransportRequest request, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    { await ObserveAsync(connection, xai); return "job"; }
    public async Task<ImageTransportJobSnapshot> GetJobAsync(ImageConnection connection, string providerJobId, ImageWorkflow workflow, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    { await ObserveAsync(connection, xai); return new(ImageTransportJobState.Completed, Outputs); }
    public async Task<ImageTransportDownload> DownloadAsync(ImageConnection connection, ImageTransportOutput output, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    { await ObserveAsync(connection, xai); Downloads++; return new(output.FileName is null ? null : "image/webp", new byte[] { 7, 8 }); }
    public Task<bool> TryCancelAsync(ImageConnection connection, string providerJobId, bool interruptRunning, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    { Cancellations++; return Task.FromResult(true); }
    private async Task ObserveAsync(ImageConnection connection, XaiFamilyBinding? xai)
    {
        if (xai is null) return;
        Bindings.Add(xai);
        LlmConnection snapshot = await xai.AcquireAsync();
        Versions.Add(snapshot.Version!);
        Assert.Equal("token", (await xai.Authorization.GetAsync()).Value);
        Assert.Same(xai.Authorization, connection.Authorization);
        HeaderValues.Add(connection.AdditionalHeaders["X-Test"]);
    }
    public void Dispose() => Disposals++;
}

internal sealed class XaiSource : ILlmConnectionSource, IDisposable
{
    public LlmConnection Current { get; set; } = new() { Version = "v1", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Xai = new(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "token", AccountId = "account" } }.Serialize() };
    public int Reads { get; private set; }
    public int Saves { get; private set; }
    public int Disposals { get; private set; }
    public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) { Reads++; return Task.FromResult<LlmConnection?>(Current); }
    public Task SaveCredentialAsync(string connectionId, string credential, string expectedVersion, CancellationToken cancellationToken = default) { Saves++; return Task.CompletedTask; }
    public void Dispose() => Disposals++;
}

internal sealed class ImageTestResources : IInferenceResourceAccess, IDisposable
{
    public string WorkspaceId => "workspace";
    public string SessionId => "session";
    public string Outcome { get; init; } = "success";
    public IOException OperationFailure { get; } = new("Controlled resource failure.");
    public IOException CleanupFailure { get; } = new("Controlled cleanup failure.");
    public InferenceResourceOperationException AuthoritativeCommitFailure { get; } = new(InferenceFailureCode.AuthenticationRequired,
        "Controlled authoritative refusal.", new(InferenceResourceOperationPhase.Commit, new("workspace", "output-1"), InferenceResourceCommitOutcome.NotCommitted, 2));
    public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? CommitRelease { get; init; }
    public List<Writer> Writers { get; } = [];
    public Func<InferenceResourceReference, CancellationToken, Task<IInferenceResourceReadHandle>>? ReadProvider { get; set; }
    public Dictionary<InferenceResourceReference, (InferenceResourceDescriptor Resource, byte[] Data)> Committed { get; } = [];
    public List<Reader> Readers { get; } = [];
    public int Disposals { get; private set; }
    public int SeparateSelectionCalls { get; private set; }
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
    {
        if (ReadProvider is not null) return ReadProvider(reference, cancellationToken);
        var stored = Committed[reference];
        var reader = new Reader(stored.Resource, new MemoryStream(stored.Data, writable: false));
        Readers.Add(reader);
        return Task.FromResult<IInferenceResourceReadHandle>(reader);
    }
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
    {
        if (Outcome == "open") throw OperationFailure;
        var writer = new Writer(this, resource, Writers.Count + 1);
        Writers.Add(writer);
        return Task.FromResult<IInferenceResourceWriteHandle>(writer);
    }
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default)
    { SeparateSelectionCalls++; throw new NotSupportedException(); }
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() => Disposals++;
    internal sealed class Writer : IInferenceResourceWriteHandle
    {
        private readonly ImageTestResources _owner;
        private readonly InferenceResourceCreate _create;
        private readonly int _revision;
        public Writer(ImageTestResources owner, InferenceResourceCreate create, int revision)
        { _owner = owner; _create = create; _revision = revision; Reference = new(owner.WorkspaceId, "output-" + revision); Content = new OutputStream(owner); }
        public InferenceResourceReference Reference { get; }
        public Stream Content { get; }
        public MemoryStream Bytes => (MemoryStream)Content;
        public int Commits { get; private set; }
        public int Disposals { get; private set; }
        public bool Selected { get; private set; }
        public async Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
        {
            Commits++;
            Assert.Equal(Bytes.Length, bytesWritten);
            _owner.CommitEntered.TrySetResult();
            if (_owner.CommitRelease is not null) await _owner.CommitRelease.Task;
            if (_owner.Outcome is "commit" or "commit-cleanup") throw _owner.OperationFailure;
            if (_owner.Outcome == "commit-refused") throw _owner.AuthoritativeCommitFailure;
            Selected = options?.SelectForSession == true;
            var descriptor = new InferenceResourceDescriptor(Reference, _create.SessionId, _create.FileName, _create.ContentType, bytesWritten);
            _owner.Committed[Reference] = (descriptor, Bytes.ToArray());
            return new(descriptor,
                _owner.Outcome == "selection" ? null : new(_owner.WorkspaceId, _owner.SessionId, _revision, [Reference]));
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (_owner.Outcome is "commit-cleanup" or "cleanup") return ValueTask.FromException(_owner.CleanupFailure);
            Content.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    internal sealed class Reader(InferenceResourceDescriptor resource, Stream content) : IInferenceResourceReadHandle
    {
        public InferenceResourceDescriptor Resource { get; } = resource;
        public Stream Content { get; } = content;
        public int Disposals { get; private set; }
        public Exception? CleanupFailure { get; init; }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            Content.Dispose();
            return CleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(CleanupFailure);
        }
    }
    private sealed class OutputStream(ImageTestResources owner) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.Outcome == "write" ? ValueTask.FromException(owner.OperationFailure) : base.WriteAsync(buffer, cancellationToken);
    }
}
