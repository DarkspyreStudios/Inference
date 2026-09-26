using System.Globalization;

namespace Darkspyre.Inference.Common.Setup;

/// <summary>
/// The editable settings for one provider entry. This is plain data: a field edit changes only this
/// object and never calls a provider or writes storage. <see cref="Steps"/> and <see cref="Actions"/>
/// are rebuilt from the current values on each read, so show/hide and enable rules run locally. A
/// settings manager runs the actions, answers choice queries and saves the object.
/// </summary>
public abstract class InferenceSettings
{
    protected InferenceSettings(InferenceSetupDefinition definition, SetupConfigurationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        Definition = definition;
        Identity = identity;
    }

    public InferenceSetupDefinition Definition { get; }

    public SetupConfigurationIdentity Identity { get; }

    public IReadOnlyList<SetupStep> Steps => CreateSteps();

    public IReadOnlyList<SetupActionDescriptor> Actions => CreateActions();

    public SetupStatus Status { get; set; } = new();

    public SetupInteraction? Interaction { get; set; }

    /// <summary>True once these settings have been saved or a save outcome is uncertain; they are not saved again.</summary>
    public bool IsSaved { get; internal set; }

    /// <summary>A host write may have occurred; read the saved entry back before trying again.</summary>
    public bool SaveOutcomeUncertain { get; internal set; }

    public bool HasUnsavedChanges => !IsSaved && IsDraftDirty;

    /// <summary>Every visible required field has a value, and a provider with sign-in has signed in.</summary>
    public bool CanSave => !IsSaved && RequiredFieldsHaveValues() && IsSignInSatisfied && Validate().IsValid;

    public SetupValidation Validate() => ValidateDraft();

    protected abstract IReadOnlyList<SetupStep> CreateSteps();

    protected abstract IReadOnlyList<SetupActionDescriptor> CreateActions();

    protected abstract SetupValidation ValidateDraft();

    protected abstract bool IsDraftDirty { get; }

    /// <summary>A provider whose settings require sign-in reports whether it has succeeded.</summary>
    protected virtual bool IsSignInSatisfied => true;

    /// <summary>Choices this object can answer from its own values, or null when the provider must be asked.</summary>
    protected internal virtual SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) => null;

    /// <summary>Applies a local edit and marks observations that depended on the old values as stale.</summary>
    protected SetupEditResult Edit(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change();
        OnDraftEdited();
        return SetupEditResult.Applied;
    }

    protected virtual void OnDraftEdited()
    {
        Status = Status with
        {
            Authenticated = Stale(Status.Authenticated),
            Verified = Stale(Status.Verified),
            Ready = Stale(Status.Ready)
        };
    }

    protected static SetupObservation Stale(SetupObservation observation) => observation.IsApplicable
        ? observation with { IsStale = true }
        : observation;

    /// <summary>Filters <paramref name="choices"/> by the query's search text and returns the requested page.</summary>
    public static SetupChoicePage Page(SetupChoiceQuery query, IEnumerable<SetupChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(choices);
        if (!int.TryParse(query.Continuation ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
        {
            return new([], State: SetupChoiceState.Failed);
        }

        SetupChoice[] matches = choices.Where(choice => choice.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || choice.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(matches.Skip(offset).Take(query.PageSize).ToArray(),
            offset + query.PageSize < matches.Length ? (offset + query.PageSize).ToString(CultureInfo.InvariantCulture) : null);
    }

    internal SetupField? FindField(string fieldId) =>
        AllFields().FirstOrDefault(field => string.Equals(field.Description.Id, fieldId, StringComparison.Ordinal));

    internal SetupActionDescriptor? FindAction(string actionId) =>
        Actions.Concat(InteractionActions()).FirstOrDefault(action => string.Equals(action.Id, actionId, StringComparison.Ordinal));

    private IEnumerable<SetupActionDescriptor> InteractionActions() => Interaction switch
    {
        SetupDeviceAuthorization device => [device.Continue, device.Cancel],
        SetupBrowserAuthorization browser => [browser.Continue, browser.Cancel],
        SetupExternalPrerequisite prerequisite => [prerequisite.Recheck],
        _ => []
    };

    private IEnumerable<SetupField> AllFields()
    {
        IEnumerable<SetupField> fields = Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).SelectMany(Flatten);
        return Interaction is SetupBrowserAuthorization browser ? fields.Append(browser.Callback) : fields;
    }

    private static IEnumerable<SetupField> Flatten(SetupField field)
    {
        yield return field;
        if (field is SetupRepeatableGroupField group)
        {
            foreach (SetupField child in group.Rows.SelectMany(row => row.Fields).SelectMany(Flatten))
            {
                yield return child;
            }
        }
    }

    private bool RequiredFieldsHaveValues() => Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
        .SelectMany(Flatten)
        .Where(field => field.Description is { IsRequired: true, IsVisible: true, IsEnabled: true })
        .All(HasValue);

    private static bool HasValue(SetupField field) => field switch
    {
        SetupTextField text => !string.IsNullOrWhiteSpace(text.Value),
        SetupChoiceField choice => !string.IsNullOrWhiteSpace(choice.Value),
        SetupUriField uri => uri.Value is not null,
        SetupExecutableField executable => !string.IsNullOrWhiteSpace(executable.Value.Value),
        SetupIntegerField integer => integer.Value is not null,
        SetupDecimalField number => number.Value is not null,
        SetupSecretField secret => secret.Change == SetupSecretEditKind.Replace || secret.HasValue && secret.Change != SetupSecretEditKind.Clear,
        _ => true
    };
}

/// <summary>Settings that describe their saved changes as <typeparamref name="TChanges"/>.</summary>
public abstract class InferenceSettings<TChanges>(InferenceSetupDefinition definition, SetupConfigurationIdentity identity)
    : InferenceSettings(definition, identity)
{
    /// <summary>The typed changes the host commits. Computed locally from the current values.</summary>
    protected internal abstract TChanges CreateChanges();
}
