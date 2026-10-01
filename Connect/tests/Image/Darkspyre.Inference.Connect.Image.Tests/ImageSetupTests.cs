using System.Text.Json;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ImageSetupTests
{
    [Fact]
    public async Task MissingWorkflowCatalogIsUnavailableNotAnEmptySuccessfulPicker()
    {
        var service = new ImageProviderService([new Transport()]);
        ImageSettingsManager manager = service.CreateSettingsManager(new Target());
        ImageSettings settings = await manager.OpenAsync(ImageTransport.ComfyUi, new(SetupChangeIntent.Create, "id", null));
        SetupChoiceField field = Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == "workflow");
        Assert.False(field.Description.IsEnabled);
        Assert.Equal(SetupChoiceState.Unavailable, (await manager.QueryChoicesAsync(settings, "workflow", new())).State);
        Assert.False(settings.Validate().IsValid);
    }

    [Fact]
    public async Task WorkflowSelectionIsResolvedAndValidatedBeforeCommitWithoutGenerating()
    {
        var transport = new Transport();
        var target = new Target();
        var (manager, settings) = await NewAsync(transport, target);
        Assert.Equal(SetupEditState.Applied, Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == "workflow").SetValue("workflow").State);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckPrerequisites).Id)).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal("workflow", target.Changes!.Configuration.Workflow.Id);
        Assert.Equal(0, transport.HealthCalls);
        Assert.Equal(0, transport.SubmitCalls);
    }

    [Fact]
    public async Task HealthAndGenerationAreSeparateExplicitActions()
    {
        var transport = new Transport();
        var (manager, settings) = await NewAsync(transport, new Target());
        Assert.Single(Fields(settings).OfType<SetupChoiceField>(), field => field.Description.Id == "workflow").SetValue("workflow");
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckPrerequisites).Id);
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckReadiness).Id);
        Assert.Equal(1, transport.HealthCalls);
        Assert.Equal(0, transport.SubmitCalls);
        Assert.Null(settings.Status.Ready.Value);
        Assert.True(Action(settings, SetupActionPurpose.Test).SideEffects.HasFlag(SetupSideEffect.PotentialBilling));
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, Action(settings, SetupActionPurpose.Test).Id)).State);
        Assert.Equal(1, transport.SubmitCalls);
        Assert.True(settings.Status.Ready.Value);
    }

    [Fact]
    public async Task ExistingHeaderValuesAreMaskedAndUntouchedUnlessExplicitlyChanged()
    {
        var transport = new Transport();
        var target = new Target();
        var service = new ImageProviderService([transport]);
        var existing = new ImageConnection
        { Transport = ImageTransport.ComfyUi, Endpoint = "http://localhost:8188", AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "saved-secret" } };
        ImageSettingsManager manager = service.CreateSettingsManager(target, new Workflows());
        ImageSettings settings = await manager.OpenAsync(ImageTransport.ComfyUi, new(SetupChangeIntent.Edit, "id", "v1"), existing, new(ImageWorkflowOrigin.Host, "workflow"));
        SetupRepeatableGroupField headers = Assert.Single(Fields(settings).OfType<SetupRepeatableGroupField>());
        SetupSecretField secret = Assert.IsType<SetupSecretField>(Assert.Single(headers.Rows).Fields[1]);
        Assert.True(secret.HasValue);
        Assert.DoesNotContain("saved-secret", settings.ToString(), StringComparison.Ordinal);
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckPrerequisites).Id);
        await manager.SaveAsync(settings);
        Assert.Empty(target.Changes!.Headers.Set);
        Assert.Empty(target.Changes.Headers.Remove);
        Assert.Equal("saved-secret", existing.AdditionalHeaders["Authorization"]);
    }

    [Fact]
    public async Task DirtyTracksEditableFieldsAndHeaderRowsButNotHealth()
    {
        var (manager, settings) = await NewAsync(new Transport(), new Target());
        Assert.False(settings.HasUnsavedChanges);
        await manager.RunAsync(settings, Action(settings, SetupActionPurpose.CheckReadiness).Id);
        Assert.False(settings.HasUnsavedChanges);
        SetupIntegerField Timeout() => Assert.Single(Fields(settings).OfType<SetupIntegerField>());
        long? baseline = Timeout().Value;
        Timeout().SetValue(123);
        Assert.True(settings.HasUnsavedChanges);
        Timeout().SetValue(baseline);
        Assert.False(settings.HasUnsavedChanges);
        Assert.Single(Fields(settings).OfType<SetupRepeatableGroupField>()).Add();
        Assert.True(settings.HasUnsavedChanges);
        SetupRepeatableGroupField group = Assert.Single(Fields(settings).OfType<SetupRepeatableGroupField>());
        group.Remove(Assert.Single(group.Rows).Id);
        Assert.False(settings.HasUnsavedChanges);
    }

    private static async Task<(ImageSettingsManager Manager, ImageSettings Settings)> NewAsync(Transport transport, Target target)
    {
        ImageSettingsManager manager = new ImageProviderService([transport]).CreateSettingsManager(target, new Workflows());
        return (manager, await manager.OpenAsync(ImageTransport.ComfyUi, new(SetupChangeIntent.Create, "id", null)));
    }
    private static IEnumerable<SetupField> Fields(ImageSettings settings) => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static SetupActionDescriptor Action(ImageSettings settings, SetupActionPurpose purpose) => Assert.Single(settings.Actions, action => action.Purpose == purpose);

    private sealed class Target : ISetupCommitTarget<ImageSetupChanges>
    {
        public Func<ImageSetupChanges, Task<SetupCommitResult>>? OnCommit { get; init; }
        public ImageSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(ImageSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return OnCommit?.Invoke(changes) ?? Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }

    private sealed class Workflows : IImageWorkflowChoiceSource
    {
        public ImageTransport Kind { get; init; } = ImageTransport.ComfyUi;
        public Task<SetupChoicePage> GetChoicesAsync(ImageTransport transport, SetupChoiceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new SetupChoicePage([new("workflow", "Workflow")]));
        public Task<ImageWorkflow?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default) => Task.FromResult<ImageWorkflow?>(new()
        { Id = workflowId, Label = "Workflow", Transport = Kind, TransportConfiguration = JsonSerializer.SerializeToElement(new { }) });
    }

    private sealed class Transport : IImageTransport
    {
        public ImageTransport Kind { get; init; } = ImageTransport.ComfyUi;
        public Func<ImageConnection, Task<ImageProviderHealth>>? OnHealth { get; init; }
        public IReadOnlyList<ImageWorkflow> BuiltInWorkflows { get; init; } = [];
        public ImageTransportPreparationRequest? Prepared { get; private set; }
        public int HealthCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public ImageTransportDescriptor Descriptor => new(Kind, "Controlled transport", "Managed fixture", new(false, false, false));

        public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
            => Task.FromResult(BuiltInWorkflows);
        public Task<ImageCapabilities> GetCapabilitiesAsync(ImageConnection connection, ImageWorkflow workflow, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ImageCapabilities { SupportedSizes = [new(workflow.DefaultWidth, workflow.DefaultHeight)], SupportsNegativePrompt = true, SupportsSeed = true });
        public Task<ImagePreparedReference> ResolveReferenceAsync(ImageConnection connection, InferenceMediaSource source, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) { Prepared = request; return new("{}"); }
        public Task<ImageProviderHealth> TestAsync(ImageConnection connection, ImageWorkflow? workflow = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
        { HealthCalls++; return OnHealth?.Invoke(connection) ?? Task.FromResult(new ImageProviderHealth(true, "OK")); }
        public Task<string> SubmitAsync(ImageConnection connection, PreparedImageTransportRequest request, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
        { SubmitCalls++; return Task.FromResult("job"); }
        public Task<ImageTransportJobSnapshot> GetJobAsync(ImageConnection connection, string providerJobId, ImageWorkflow workflow, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ImageTransportJobSnapshot(ImageTransportJobState.Completed, [new("image.png", new("owner", "identity"), JsonElement.Parse("{}"))]));
        public Task<ImageTransportDownload> DownloadAsync(ImageConnection connection, ImageTransportOutput output, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult(new ImageTransportDownload("image/png", new byte[] { 1, 2, 3 }));
        public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult<IImageTransportProgressConnection?>(null);
        public Task<ImagePreparedReference> UploadReferenceAsync(ImageConnection connection, ImageReference reference, Stream content, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryCancelAsync(ImageConnection connection, string providerJobId, bool interruptRunning, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
