using System.Text.Json;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageSetupTests
{
    [Fact]
    public async Task MissingWorkflowCatalogIsUnavailableNotAnEmptySuccessfulPicker()
    {
        var service = new ImageProviderService([new Transport()]);
        await using ImageSetupSession session = service.CreateSetupSession(ImageTransport.ComfyUi,
            new(SetupChangeIntent.Create, "id", null), new Target());
        SetupChoiceField field = Assert.Single(Fields(session).OfType<SetupChoiceField>());
        Assert.False(field.Description.IsEnabled);
        Assert.Equal(SetupChoiceState.Unavailable, (await field.GetChoicesAsync(new(0))).State);
        Assert.False((await session.ValidateAsync()).IsValid);
    }

    [Fact]
    public async Task WorkflowSelectionIsResolvedAndValidatedBeforeCommitWithoutGenerating()
    {
        var transport = new Transport();
        var target = new Target();
        await using ImageSetupSession session = New(transport, target);
        Assert.Equal(SetupEditState.Applied, Assert.Single(Fields(session).OfType<SetupChoiceField>()).SetValue("workflow").State);
        Assert.Equal(SetupCommitState.Invalid, (await session.CommitAsync()).State);
        Assert.Equal(SetupActionState.Completed, (await Action(session, SetupActionPurpose.CheckPrerequisites).ExecuteAsync()).State);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal("workflow", target.Changes!.Configuration.WorkflowId);
        Assert.Equal(0, transport.HealthCalls);
        Assert.Equal(0, transport.SubmitCalls);
    }

    [Fact]
    public async Task HealthAndGenerationAreSeparateExplicitActions()
    {
        var transport = new Transport();
        await using ImageSetupSession session = New(transport, new Target());
        Assert.Single(Fields(session).OfType<SetupChoiceField>()).SetValue("workflow");
        await Action(session, SetupActionPurpose.CheckPrerequisites).ExecuteAsync();
        await Action(session, SetupActionPurpose.CheckReadiness).ExecuteAsync();
        Assert.Equal(1, transport.HealthCalls);
        Assert.Equal(0, transport.SubmitCalls);
        Assert.Null(session.Snapshot.Status.Ready.Value);
        Assert.True(Action(session, SetupActionPurpose.Test).Descriptor.SideEffects.HasFlag(SetupSideEffect.PotentialBilling));
        Assert.Equal(SetupActionState.Completed, (await Action(session, SetupActionPurpose.Test).ExecuteAsync()).State);
        Assert.Equal(1, transport.SubmitCalls);
        Assert.True(session.Snapshot.Status.Ready.Value);
    }

    [Fact]
    public async Task ExistingHeaderValuesAreMaskedAndUntouchedUnlessExplicitlyChanged()
    {
        var transport = new Transport();
        var target = new Target();
        var service = new ImageProviderService([transport]);
        var existing = new ImageConnection
        { Transport = ImageTransport.ComfyUi, Endpoint = "http://localhost:8188", AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "saved-secret" } };
        await using ImageSetupSession session = service.CreateSetupSession(ImageTransport.ComfyUi,
            new(SetupChangeIntent.Edit, "id", "v1"), target, new Workflows(), existing, "workflow");
        SetupRepeatableGroupField headers = Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>());
        SetupSecretField secret = Assert.IsType<SetupSecretField>(Assert.Single(headers.Rows).Fields[1]);
        Assert.True(secret.HasValue);
        Assert.DoesNotContain("saved-secret", session.Snapshot.ToString(), StringComparison.Ordinal);
        await Action(session, SetupActionPurpose.CheckPrerequisites).ExecuteAsync();
        await session.CommitAsync();
        Assert.Empty(target.Changes!.Headers.Set);
        Assert.Empty(target.Changes.Headers.Remove);
        Assert.Equal("saved-secret", existing.AdditionalHeaders["Authorization"]);
    }

    [Fact]
    public async Task DirtyTracksEditableFieldsAndHeaderRowsButNotHealth()
    {
        await using ImageSetupSession session = New(new Transport(), new Target());
        Assert.False(session.Snapshot.IsDirty);
        await Action(session, SetupActionPurpose.CheckReadiness).ExecuteAsync();
        Assert.False(session.Snapshot.IsDirty);
        SetupIntegerField Timeout() => Assert.Single(Fields(session).OfType<SetupIntegerField>());
        long? baseline = Timeout().Value;
        Timeout().SetValue(123);
        Assert.True(session.Snapshot.IsDirty);
        Timeout().SetValue(baseline);
        Assert.False(session.Snapshot.IsDirty);
        Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>()).Add();
        Assert.True(session.Snapshot.IsDirty);
        SetupRepeatableGroupField group = Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>());
        group.Remove(Assert.Single(group.Rows).Id);
        Assert.False(session.Snapshot.IsDirty);
    }

    private static ImageSetupSession New(Transport transport, Target target) => new ImageProviderService([transport])
        .CreateSetupSession(ImageTransport.ComfyUi, new(SetupChangeIntent.Create, "id", null), target, new Workflows());
    private static IEnumerable<SetupField> Fields(ImageSetupSession session) => session.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static ISetupAction Action(ImageSetupSession session, SetupActionPurpose purpose) => Assert.Single(session.Snapshot.Actions, action => action.Descriptor.Purpose == purpose);

    private sealed class Target : ISetupCommitTarget<ImageSetupChanges>
    {
        public ImageSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(ImageSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }

    private sealed class Workflows : IImageWorkflowChoiceSource
    {
        public Task<SetupChoicePage> GetChoicesAsync(ImageTransport transport, SetupChoiceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new SetupChoicePage(query.ExpectedRevision, [new("workflow", "Workflow")]));
        public Task<ImageWorkflow?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default) => Task.FromResult<ImageWorkflow?>(new()
        { Id = workflowId, Label = "Workflow", Transport = ImageTransport.ComfyUi, TransportConfiguration = JsonSerializer.SerializeToElement(new { }) });
    }

    private sealed class Transport : IImageTransport
    {
        public int HealthCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public ImageTransportDescriptor Descriptor { get; } = new(ImageTransport.ComfyUi, "ComfyUI", "Test", new(false, false, false));
        public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) => new("{}");
        public Task<ImageProviderHealth> TestAsync(ImageConnection connection, ImageWorkflow? workflow = null, CancellationToken cancellationToken = default)
        { HealthCalls++; return Task.FromResult(new ImageProviderHealth(true, "OK")); }
        public Task<string> SubmitAsync(ImageConnection connection, PreparedImageTransportRequest request, CancellationToken cancellationToken = default)
        { SubmitCalls++; return Task.FromResult("job"); }
        public Task<ImageTransportJobSnapshot> GetJobAsync(ImageConnection connection, string providerJobId, ImageWorkflow workflow, CancellationToken cancellationToken = default)
            => Task.FromResult(new ImageTransportJobSnapshot(ImageTransportJobState.Completed, new("image.png", "{}")));
        public Task<byte[]> DownloadAsync(ImageConnection connection, ImageTransportOutput output, CancellationToken cancellationToken = default) => Task.FromResult<byte[]>([1, 2, 3]);
        public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(ImageConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IImageTransportProgressConnection?>(null);
        public Task UploadReferenceAsync(ImageConnection connection, ImageReference reference, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryCancelAsync(ImageConnection connection, string providerJobId, bool interruptRunning, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
