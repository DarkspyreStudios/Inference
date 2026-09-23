namespace Darkspyre.Inference.Common.Setup;

public sealed record SetupFieldDescription(string Id, string DisplayName)
{
    public string HelpText { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
}

/// <summary>A safe snapshot plus a provider-bound edit operation, never a configuration bag.</summary>
public abstract class SetupField(SetupFieldDescription description)
{
    public SetupFieldDescription Description { get; } = description;
}

public abstract class SetupValueField<T>(
    SetupFieldDescription description, T value, T defaultValue, Func<T, SetupEditResult> edit)
    : SetupField(description)
{
    public T Value { get; } = value;
    public T DefaultValue { get; } = defaultValue;
    public SetupEditResult SetValue(T value) => Description.IsReadOnly || !Description.IsEnabled || !Description.IsVisible
        ? new(SetupEditState.Invalid, [new("field-unavailable", "This field cannot be edited.", Description.Id)])
        : edit(value);
}

public sealed class SetupTextField(
    SetupFieldDescription description, string value, string defaultValue, Func<string, SetupEditResult> edit,
    int? maximumLength = null) : SetupValueField<string>(description, value, defaultValue, edit)
{
    public int? MaximumLength { get; } = maximumLength;
}

public sealed class SetupUriField(
    SetupFieldDescription description, Uri? value, Uri? defaultValue, Func<Uri?, SetupEditResult> edit)
    : SetupValueField<Uri?>(description, value, defaultValue, edit);

public sealed record SetupExecutableReference(string Value);

public sealed class SetupExecutableField(
    SetupFieldDescription description, SetupExecutableReference value, SetupExecutableReference defaultValue,
    Func<SetupExecutableReference, SetupEditResult> edit)
    : SetupValueField<SetupExecutableReference>(description, value, defaultValue, edit);

public sealed class SetupIntegerField(
    SetupFieldDescription description, long? value, long? defaultValue, Func<long?, SetupEditResult> edit,
    long? minimum = null, long? maximum = null)
    : SetupValueField<long?>(description, value, defaultValue, edit)
{
    public long? Minimum { get; } = minimum;
    public long? Maximum { get; } = maximum;
}

public sealed class SetupDecimalField(
    SetupFieldDescription description, decimal? value, decimal? defaultValue, Func<decimal?, SetupEditResult> edit,
    decimal? minimum = null, decimal? maximum = null, decimal step = 1)
    : SetupValueField<decimal?>(description, value, defaultValue, edit)
{
    public decimal? Minimum { get; } = minimum;
    public decimal? Maximum { get; } = maximum;
    public decimal Step { get; } = step;
}

public sealed class SetupBooleanField(
    SetupFieldDescription description, bool value, bool defaultValue, Func<bool, SetupEditResult> edit)
    : SetupValueField<bool>(description, value, defaultValue, edit);

public enum SetupSecretEditKind { Keep, Replace, Clear }

/// <summary>One-way secret input. Never place this value in snapshots, diagnostics or logs.</summary>
public sealed class SetupSecretEdit
{
    private SetupSecretEdit(SetupSecretEditKind kind, string? value) { Kind = kind; Value = value; }
    public SetupSecretEditKind Kind { get; }
    public string? Value { get; }
    public static SetupSecretEdit Keep { get; } = new(SetupSecretEditKind.Keep, null);
    public static SetupSecretEdit Clear { get; } = new(SetupSecretEditKind.Clear, null);
    public static SetupSecretEdit Replace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SetupSecretEditKind.Replace, value);
    }

    public override string ToString() => Kind.ToString();
}

public sealed class SetupSecretField(
    SetupFieldDescription description, bool hasValue, SetupSecretEditKind change,
    Func<SetupSecretEdit, SetupEditResult> edit) : SetupField(description)
{
    public bool HasValue { get; } = hasValue;
    public SetupSecretEditKind Change { get; } = change;
    public SetupEditResult SetValue(SetupSecretEdit value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Description.IsReadOnly || !Description.IsEnabled || !Description.IsVisible
            ? new(SetupEditState.Invalid, [new("field-unavailable", "This field cannot be edited.", Description.Id)])
            : edit(value);
    }
}

public sealed record SetupChoice(string Value, string DisplayName, string? Description = null);
public sealed record SetupChoiceQuery(long ExpectedRevision, string Search = "", string? Continuation = null, int PageSize = 50);
public enum SetupChoiceState { Completed, Stale, Unavailable, Failed, Cancelled }
public sealed record SetupChoicePage(
    long Revision, IReadOnlyList<SetupChoice> Choices, string? Continuation = null,
    SetupChoiceState State = SetupChoiceState.Completed, IReadOnlyList<SetupIssue>? Issues = null);

/// <summary>Choice-source behavior, independent of any UI framework or manual-entry permission.</summary>
public sealed record SetupChoiceCapabilities(bool SupportsSearch = false, bool SupportsPaging = false, bool SupportsRefresh = false)
{
    public static SetupChoiceCapabilities Bounded { get; } = new();
    public static SetupChoiceCapabilities Searchable { get; } = new(true, true, true);
}

public sealed class SetupChoiceField(
    SetupFieldDescription description, string value, string defaultValue, Func<string, SetupEditResult> edit,
    bool allowsManualEntry, Func<SetupChoiceQuery, CancellationToken, Task<SetupChoicePage>> getChoices,
    long revision,
    SetupSideEffect choiceSideEffects = SetupSideEffect.None,
    SetupChoiceCapabilities? capabilities = null)
    : SetupValueField<string>(description, value, defaultValue, edit)
{
    public bool AllowsManualEntry { get; } = allowsManualEntry;
    public SetupSideEffect ChoiceSideEffects { get; } = choiceSideEffects;
    public SetupChoiceCapabilities Capabilities { get; } = capabilities ?? SetupChoiceCapabilities.Bounded;
    public Task<SetupChoicePage> GetChoicesAsync(SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PageSize, 200);
        if (query.ExpectedRevision != revision) { return Task.FromResult(new SetupChoicePage(revision, [], State: SetupChoiceState.Stale)); }
        if (!Description.IsEnabled || !Description.IsVisible)
        { return Task.FromResult(new SetupChoicePage(revision, [], State: SetupChoiceState.Unavailable)); }
        return getChoices(query, cancellationToken);
    }
}

public sealed record SetupFieldRow(string Id, IReadOnlyList<SetupField> Fields);

public sealed class SetupRepeatableGroupField(
    SetupFieldDescription description, IReadOnlyList<SetupFieldRow> rows,
    Func<SetupEditResult> add, Func<string, SetupEditResult> remove) : SetupField(description)
{
    public IReadOnlyList<SetupFieldRow> Rows { get; } = rows;
    public SetupEditResult Add() => CanEdit ? add() : Unavailable;
    public SetupEditResult Remove(string rowId) => CanEdit ? remove(rowId) : Unavailable;
    private bool CanEdit => !Description.IsReadOnly && Description.IsEnabled && Description.IsVisible;
    private SetupEditResult Unavailable => new(SetupEditState.Invalid,
        [new("field-unavailable", "This group cannot be edited.", Description.Id)]);
}
