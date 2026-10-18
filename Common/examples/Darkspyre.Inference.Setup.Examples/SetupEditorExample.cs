using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Darkspyre.Inference.Tensor.LLM.Qwen;

namespace Darkspyre.Inference.Setup.Examples;

/// <summary>One provider entry being edited: the settings object and the manager that acts on it.</summary>
public sealed record SetupDraft(IInferenceSettingsManager Manager, InferenceSettings Settings);

/// <summary>Composition belongs to the host; rendering below has no provider-specific dispatch.</summary>
public static class SetupCompositionExample
{
    public static async Task<IReadOnlyList<SetupDraft>> CreateDraftsAsync(
        LlmProviderService llms, ImageProviderService images, ITensorModelPackManager packs,
        IImageWorkflowChoiceSource workflows, ISetupCommitTarget<LlmSetupChanges> llmTarget,
        ISetupCommitTarget<ImageSetupChanges> imageTarget, ISetupCommitTarget<TensorSetupChanges> tensorTarget,
        ITensorModelSource? modelSource = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(llms);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(packs);
        // No network requests, model loads or installs occur here. Nothing reaches the host until Save.
        IReadOnlyList<TensorModelPackDefinition> definitions = await packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var drafts = new List<SetupDraft>();
        LlmSettingsManager llmManager = llms.CreateSettingsManager(llmTarget);
        foreach (LlmProviderDefinition provider in llms.SupportedProviders)
        {
            drafts.Add(new(llmManager, await llmManager.OpenAsync(provider.Transport, NewEntry(), cancellationToken: cancellationToken)
                .ConfigureAwait(false)));
        }

        ImageSettingsManager imageManager = images.CreateSettingsManager(imageTarget, workflows);
        drafts.Add(new(imageManager, await imageManager.OpenAsync(ImageTransport.ComfyUi, NewEntry(), cancellationToken: cancellationToken)
            .ConfigureAwait(false)));
        TensorSettingsManager qwen = QwenSetup.CreateSettingsManager(tensorTarget, packs,
            ExampleList(definitions.Where(pack => pack.Capability == TensorCapability.Llm
                && pack.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture)), source: modelSource);
        TensorSettingsManager supra = SupraSetup.CreateSettingsManager(tensorTarget, packs,
            ExampleList(definitions.Where(pack => pack.Capability == TensorCapability.Image
                && pack.RuntimeArchitecture == TensorImageModels.SupraRuntimeArchitecture)), source: modelSource);
        TensorSettingsManager whisper = WhisperSetup.CreateSettingsManager(tensorTarget, packs,
            ExampleList(TensorSpeechModels.Whisper), source: modelSource);
        foreach (TensorSettingsManager manager in new[] { qwen, supra, whisper })
        {
            drafts.Add(new(manager, await manager.OpenAsync(NewEntry(), cancellationToken: cancellationToken).ConfigureAwait(false)));
        }

        return drafts.AsReadOnly();
    }

    /// <summary>A model list with no live model store: every definition is offered as not downloaded.</summary>
    internal static ITensorModelList ExampleList(IEnumerable<TensorModelPackDefinition> definitions) =>
        TensorModelList.Fixed(new("example store",
            definitions.Select(definition => new TensorListedModel(definition, SetupInstallState.NotDownloaded, null, null)).ToArray(), null));

    private static SetupConfigurationIdentity NewEntry() => new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null);
}

/// <summary>
/// The UI supplies reusable controls. Implementations bind the field's typed SetValue method and
/// render the settings again after edits; they do not inspect provider IDs or parse help text.
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
    void Choice(SetupChoiceField field, IInferenceSettingsManager manager, InferenceSettings settings);
    void Secret(SetupSecretField field);
    void InstallStatus(SetupInstallStatusField field);
    void BeginGroup(SetupRepeatableGroupField field);
    void BeginRow(SetupFieldRow row);
    void EndRow();
    void EndGroup();
    void EndSection();
    void EndStep();
    void Action(SetupActionDescriptor action);
    void DeviceAuthorization(SetupDeviceAuthorization interaction);
    void BrowserAuthorization(SetupBrowserAuthorization interaction);
    void ExternalPrerequisite(SetupExternalPrerequisite interaction);
    void Status(SetupStatus status, SetupValidation validation, bool canSave);
}

public static class SetupEditorExample
{
    public static void Render(SetupDraft draft, ISetupEditorControls controls)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(controls);
        InferenceSettings settings = draft.Settings;
        foreach (SetupStep step in settings.Steps)
        {
            controls.BeginStep(step);
            foreach (SetupSection section in step.Sections)
            {
                controls.BeginSection(section);
                foreach (SetupField field in section.Fields) { RenderField(field, draft, controls); }
                controls.EndSection();
            }
            controls.EndStep();
        }
        foreach (SetupActionDescriptor action in settings.Actions) { controls.Action(action); }
        switch (settings.Interaction)
        {
            case SetupDeviceAuthorization device: controls.DeviceAuthorization(device); break;
            case SetupBrowserAuthorization browser: controls.BrowserAuthorization(browser); break;
            case SetupExternalPrerequisite prerequisite: controls.ExternalPrerequisite(prerequisite); break;
        }
        controls.Status(settings.Status, settings.Validate(), settings.CanSave);
    }

    private static void RenderField(SetupField field, SetupDraft draft, ISetupEditorControls controls)
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
            case SetupChoiceField choice: controls.Choice(choice, draft.Manager, draft.Settings); break;
            case SetupSecretField secret: controls.Secret(secret); break;
            case SetupInstallStatusField install: controls.InstallStatus(install); break;
            case SetupRepeatableGroupField group:
                controls.BeginGroup(group);
                foreach (SetupFieldRow row in group.Rows)
                {
                    controls.BeginRow(row);
                    foreach (SetupField child in row.Fields) { RenderField(child, draft, controls); }
                    controls.EndRow();
                }
                controls.EndGroup();
                break;
            default: throw new NotSupportedException("The renderer needs a control for this semantic field type.");
        }
    }

    /// <summary>Authorization is supplied explicitly by the host, never inferred from clicking Save.</summary>
    public static Task<SetupActionResult> ExecuteAuthorizedAsync(SetupDraft draft, SetupActionDescriptor action,
        Func<SetupSideEffect, bool> authorize, IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(authorize);
        return authorize(action.SideEffects)
            ? draft.Manager.RunAsync(draft.Settings, action.Id, progress, cancellationToken)
            : Task.FromResult(new SetupActionResult(SetupActionState.Unavailable));
    }
}
