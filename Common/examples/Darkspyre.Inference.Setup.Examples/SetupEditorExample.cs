using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;

namespace Darkspyre.Inference.Setup.Examples;

/// <summary>Composition belongs to the host; rendering below has no provider-specific dispatch.</summary>
public static class SetupCompositionExample
{
    public static IReadOnlyList<IInferenceSetupSession> CreateDrafts(
        LlmProviderService llms, ImageProviderService images, ITensorModelPackManager packs,
        IImageWorkflowChoiceSource workflows, ISetupCommitTarget<LlmSetupChanges> llmTarget,
        ISetupCommitTarget<ImageSetupChanges> imageTarget, ISetupCommitTarget<TensorSetupChanges> tensorTarget,
        ITensorModelSource? modelSource = null)
    {
        ArgumentNullException.ThrowIfNull(llms);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(packs);
        // No network requests, model loads or installs occur here. The caller owns/disposes the sessions.
        List<IInferenceSetupSession> sessions = llms.SupportedProviders.Select(provider =>
            (IInferenceSetupSession)llms.CreateSetupSession(provider.Transport,
                new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), llmTarget)).ToList();
        sessions.Add(images.CreateSetupSession(ImageTransport.ComfyUi,
            new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), imageTarget, workflows));
        sessions.Add(QwenSetup.CreateSession(new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), tensorTarget, packs,
            offeredModels: packs.Definitions.Where(pack => pack.Capability == TensorCapability.Llm
                && pack.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture).ToArray(), source: modelSource));
        sessions.Add(SupraSetup.CreateSession(new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), tensorTarget, packs,
            offeredModels: packs.Definitions.Where(pack => pack.Capability == TensorCapability.Image
                && pack.RuntimeArchitecture == TensorImageModels.SupraRuntimeArchitecture).ToArray(), source: modelSource));
        return sessions.AsReadOnly();
    }
}

/// <summary>
/// The UI supplies reusable controls. Implementations bind the field's typed SetValue method and
/// refresh from Snapshot after edits; they do not inspect provider IDs or parse help text.
/// </summary>
public interface ISetupEditorControls
{
    void BeginStep(SetupStep setupStep);
    void BeginSection(SetupSection section);
    void Text(SetupTextField field);
    void Uri(SetupUriField field);
    void Executable(SetupExecutableField field);
    void WholeNumber(SetupIntegerField field);
    void FractionalNumber(SetupDecimalField field);
    void Toggle(SetupBooleanField field);
    void Choice(SetupChoiceField field, long revision);
    void Secret(SetupSecretField field);
    void BeginGroup(SetupRepeatableGroupField field);
    void BeginRow(SetupFieldRow row);
    void EndRow();
    void EndGroup();
    void EndSection();
    void EndStep();
    void Action(ISetupAction action);
    void DeviceAuthorization(SetupDeviceAuthorization interaction);
    void BrowserAuthorization(SetupBrowserAuthorization interaction);
    void ExternalPrerequisite(SetupExternalPrerequisite interaction);
    void Status(SetupStatus status, SetupValidation validation, bool isBusy, bool isClosed);
}

public static class SetupEditorExample
{
    public static void Render(IInferenceSetupSession session, ISetupEditorControls controls)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(controls);
        SetupSnapshot snapshot = session.Snapshot;
        foreach (SetupStep step in snapshot.Steps)
        {
            controls.BeginStep(step);
            foreach (SetupSection section in step.Sections)
            {
                controls.BeginSection(section);
                foreach (SetupField field in section.Fields) { RenderField(field, snapshot.Revision, controls); }
                controls.EndSection();
            }
            controls.EndStep();
        }
        foreach (ISetupAction action in snapshot.Actions) { controls.Action(action); }
        switch (snapshot.Interaction)
        {
            case SetupDeviceAuthorization device: controls.DeviceAuthorization(device); break;
            case SetupBrowserAuthorization browser: controls.BrowserAuthorization(browser); break;
            case SetupExternalPrerequisite prerequisite: controls.ExternalPrerequisite(prerequisite); break;
        }
        controls.Status(snapshot.Status, snapshot.Validation, snapshot.IsBusy, snapshot.IsClosed);
    }

    private static void RenderField(SetupField field, long revision, ISetupEditorControls controls)
    {
        if (!field.Description.IsVisible) { return; }
        switch (field)
        {
            case SetupTextField text: controls.Text(text); break;
            case SetupUriField uri: controls.Uri(uri); break;
            case SetupExecutableField executable: controls.Executable(executable); break;
            case SetupIntegerField integer: controls.WholeNumber(integer); break;
            case SetupDecimalField number: controls.FractionalNumber(number); break;
            case SetupBooleanField boolean: controls.Toggle(boolean); break;
            case SetupChoiceField choice: controls.Choice(choice, revision); break;
            case SetupSecretField secret: controls.Secret(secret); break;
            case SetupRepeatableGroupField group:
                controls.BeginGroup(group);
                foreach (SetupFieldRow row in group.Rows)
                {
                    controls.BeginRow(row);
                    foreach (SetupField child in row.Fields) { RenderField(child, revision, controls); }
                    controls.EndRow();
                }
                controls.EndGroup();
                break;
            default: throw new NotSupportedException("The renderer needs a control for this semantic field type.");
        }
    }

    /// <summary>Authorization is supplied explicitly by the host, never inferred from clicking Save.</summary>
    public static Task<SetupActionResult> ExecuteAuthorizedAsync(ISetupAction action,
        Func<SetupSideEffect, bool> authorize, IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(authorize);
        return authorize(action.Descriptor.SideEffects)
            ? action.ExecuteAsync(progress, cancellationToken)
            : Task.FromResult(new SetupActionResult(SetupActionState.Unavailable));
    }
}
