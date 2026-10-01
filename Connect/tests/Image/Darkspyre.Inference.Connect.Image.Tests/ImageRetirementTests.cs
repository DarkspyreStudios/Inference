using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedRetiredOwnerReleasesActualForeignBorrowedCollaborators(bool settings)
    {
        RetirementProof proof = RetireForeign(settings);
        for (int attempt = 0; proof.Generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(proof.Generation.IsAlive);
        Assert.All(proof.Collaborators, collaborator => Assert.False(collaborator.IsAlive));
        GC.KeepAlive(proof.Owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetirementProof RetireForeign(bool settings)
    {
        var generation = new CollaboratorGeneration();
        var weak = new WeakReference(generation);
        Assembly assembly = generation.LoadFromAssemblyPath(typeof(ImageRetirementTests).Assembly.Location);
        Assert.NotSame(typeof(ImageRetirementTests).Assembly, assembly);
        Assert.Equal(typeof(ImageRetirementTests).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
        object owner = CreateSharedProofOwner(assembly);
        // The foreign class shares only contract assemblies; its actual implementation stays collectible.
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(owner.GetType().Assembly));
        var borrowed = (IImageWorkflowChoiceSource)owner;
        object transportObject = Activator.CreateInstance(assembly.GetType(typeof(ImageTestTransport).FullName!, true)!)!;
        var transport = (IImageTransport)transportObject;
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(transport.GetType().Assembly));
        var collaborators = new List<WeakReference> { new(owner), new(transport) };
        var service = new ImageProviderService([transport]);
        object retained;
        if (settings)
        {
            Func<XaiConnectionSelection, XaiFamilyBinding> factory = owner.GetType().GetMethod(nameof(ImageForeignCollaborators.CreateBinding))!
                .CreateDelegate<Func<XaiConnectionSelection, XaiFamilyBinding>>(owner);
            ImageSettingsManager manager = service.CreateSettingsManager((ISetupCommitTarget<ImageSetupChanges>)owner, borrowed, factory);
            ImageSettings draft = manager.OpenAsync(ImageTransport.ComfyUi, new(SetupChangeIntent.Create, "id", null)).GetAwaiter().GetResult();
            Assert.Equal(SetupChoiceState.Completed, manager.QueryChoicesAsync(draft, "workflow", new()).GetAwaiter().GetResult().State);
            manager.Dispose();
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            retained = manager;
        }
        else
        {
            object resources = Activator.CreateInstance(assembly.GetType(typeof(ImageTestResources).FullName!, true)!)!;
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(resources.GetType().Assembly));
            collaborators.Add(new(resources));
            IImageInference inference = service.Bind(ImageOperationTests.Connection(), ImageOperationTests.Workflow(),
                resources: (IInferenceResourceAccess)resources, pollInterval: TimeSpan.Zero);
            Assert.Equal(2, inference.GenerateAsync(ImageOperationTests.Request()).GetAwaiter().GetResult().Images.Count);
            ((IDisposable)inference).Dispose();
            ((IAsyncDisposable)inference).DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(0, (int)resources.GetType().GetProperty(nameof(ImageTestResources.Disposals))!.GetValue(resources)!);
            retained = inference;
        }
        Assert.Equal(0, (int)owner.GetType().GetProperty(nameof(ImageForeignCollaborators.Disposals))!.GetValue(owner)!);
        Assert.Equal(0, (int)transportObject.GetType().GetProperty(nameof(ImageTestTransport.Disposals))!.GetValue(transportObject)!);
        generation.Unload();
        return new(retained, weak, collaborators.ToArray());
    }

    private static object CreateSharedProofOwner(Assembly assembly)
        => Activator.CreateInstance(assembly.GetType(typeof(ImageForeignCollaborators).FullName!, true)!)!;

    private sealed record RetirementProof(object Owner, WeakReference Generation, WeakReference[] Collaborators);

    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true
                ? Default.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == name.Name) : null;
    }
}

public sealed class ImageForeignCollaborators : IImageWorkflowChoiceSource, ISetupCommitTarget<ImageSetupChanges>, IDisposable
{
    public int Disposals { get; private set; }
    public int BindingCalls { get; private set; }
    public Task<SetupChoicePage> GetChoicesAsync(ImageTransport transport, SetupChoiceQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(new SetupChoicePage([new("workflow", "Foreign workflow")]));
    public Task<ImageWorkflow?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult<ImageWorkflow?>(ImageOperationTests.Workflow());
    public Task<SetupCommitResult> CommitAsync(ImageSetupChanges changes, CancellationToken cancellationToken = default)
        => Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2"));
    public XaiFamilyBinding CreateBinding(XaiConnectionSelection selection)
    { BindingCalls++; throw new InvalidOperationException("No authentication is required by this managed retirement observation."); }
    public void Dispose() => Disposals++;
}
