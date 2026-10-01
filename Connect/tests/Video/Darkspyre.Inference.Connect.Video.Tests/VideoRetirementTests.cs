using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoRetirementTests
{
    [Fact]
    public async Task DisposedFacadeAndManagerRemainHeldWhileActualForeignBorrowersCollect()
    {
        (WeakReference generation, object facade, VideoSettingsManager manager) = await ExerciseForeignBorrowers();
        for (int i = 0; generation.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        Assert.False(generation.IsAlive);
        GC.KeepAlive(facade); GC.KeepAlive(manager);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, object, VideoSettingsManager)> ExerciseForeignBorrowers()
    {
        var generation = new BorrowerGeneration();
        var weak = new WeakReference(generation);
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(VideoForeignBorrowers).Assembly.Location);
        Assert.NotSame(typeof(VideoForeignBorrowers).Assembly, fixture);
        Assert.Equal(typeof(VideoForeignBorrowers).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Type type = fixture.GetType(typeof(VideoForeignBorrowers).FullName!, true)!;
        object borrower = Activator.CreateInstance(type)!;
        Assert.Same(fixture, borrower.GetType().Assembly);
        var source = new VideoOperationTests.Source(); await using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("connection"), access);
        type.GetMethod(nameof(VideoForeignBorrowers.Configure))!.Invoke(borrower, [binding]);
        var factory = (Func<XaiConnectionSelection, XaiFamilyBinding>)Delegate.CreateDelegate(typeof(Func<XaiConnectionSelection, XaiFamilyBinding>), borrower,
            type.GetMethod(nameof(VideoForeignBorrowers.Binding))!);
        Assert.Same(fixture, factory.Method.Module.Assembly);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(factory.Method.Module.Assembly));
        var service = new VideoProviderService([(IVideoTransport)borrower]);
        VideoSettingsManager manager = service.CreateSettingsManager((ISetupCommitTarget<VideoSetupChanges>)borrower, factory);
        var connection = new VideoConnection { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = new("connection") };
        VideoSettings settings = await manager.OpenAsync(VideoTransport.XaiImagine, new(SetupChangeIntent.Edit, "entry", "v"), new(connection, "model"));
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "capabilities")).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        IVideoInference facade = service.Bind(connection, binding, (IInferenceResourceAccess)borrower);
        await facade.GetCapabilitiesAsync();
        await manager.DisposeAsync(); await ((IAsyncDisposable)facade).DisposeAsync();
        Assert.Equal(0, type.GetProperty(nameof(VideoForeignBorrowers.Disposes))!.GetValue(borrower));
        Assert.Equal(1, type.GetProperty(nameof(VideoForeignBorrowers.FactoryCalls))!.GetValue(borrower));
        Assert.Equal(1, type.GetProperty(nameof(VideoForeignBorrowers.Commits))!.GetValue(borrower));
        generation.Unload();
        return (weak, facade, manager);
    }
    private sealed class BorrowerGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(VideoForeignBorrowers).Assembly.GetName().Name) return null;
            return Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        }
    }
}

/// <summary>Actual foreign implementations borrow default-context public contracts, not copied DTOs.</summary>
public sealed class VideoForeignBorrowers : IVideoTransport, IInferenceResourceAccess, ISetupCommitTarget<VideoSetupChanges>, IDisposable
{
    private XaiFamilyBinding? _binding;
    public int Disposes { get; private set; }
    public int FactoryCalls { get; private set; }
    public int Commits { get; private set; }
    public void Configure(XaiFamilyBinding binding) => _binding = binding;
    public XaiFamilyBinding Binding(XaiConnectionSelection selection)
    {
        FactoryCalls++;
        if (selection != _binding!.Selection) throw new ArgumentException("Mismatched fixture selection.");
        return _binding;
    }
    public VideoTransport Transport => VideoTransport.XaiImagine;
    public InferenceSetupDefinition SetupDefinition { get; } = new(new("foreign-controlled-video", "Foreign Controlled Video",
        Common.InferenceImplementationKind.Connect, Common.InferenceTask.VideoGeneration), "Controlled managed collaborator.");
    public Task<VideoCapabilities> GetCapabilitiesAsync(VideoConnection connection, string? modelId = null,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult(new VideoCapabilities());
    public Task<VideoJob> SubmitAsync(VideoConnection connection, VideoRequest request, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<VideoJobSnapshot> GetAsync(VideoConnection connection, VideoJob job, XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<Stream> DownloadAsync(VideoConnection connection, VideoOutput output, XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<SetupCommitResult> CommitAsync(VideoSetupChanges changes, CancellationToken cancellationToken = default)
    { Commits++; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed)); }
    public string WorkspaceId => "w";
    public string SessionId => "s";
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public void Dispose() => Disposes++;
}
