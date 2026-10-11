using Darkspyre.Inference.Common;
namespace Darkspyre.Inference.Common.Setup;

/// <summary>The kind of path picker a text field offers.</summary>
public enum SetupPathSelectionKind { /** <summary>Picks a file.</summary> */ File, /** <summary>Picks a folder.</summary> */ Folder }

/// <summary>Identity and presentation state shared by every setup field.</summary>
/// <param name="Id">Identifier of the field within its settings object.</param>
/// <param name="DisplayName">Label shown to the user.</param>
public sealed record SetupFieldDescription(string Id, string DisplayName)
{
    /// <summary>Explanatory text shown with the field, or empty when there is none.</summary>
    public string HelpText { get; init; } = string.Empty;
    /// <summary>True when the field must have a value.</summary>
    public bool IsRequired { get; init; }
    /// <summary>True when the field shows its value but rejects edits.</summary>
    public bool IsReadOnly { get; init; }
    /// <summary>False hides the field and makes it reject edits.</summary>
    public bool IsVisible { get; init; } = true;
    /// <summary>False disables the field and makes it reject edits.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>One typed field of a settings object. Its edit changes only that object; never a configuration bag.</summary>
public abstract class SetupField(SetupFieldDescription description)
{
    /// <summary>Identity and presentation state of this field.</summary>
    public SetupFieldDescription Description { get; } = description;
}

/// <summary>A setup field that holds one value of type <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current value.</param>
/// <param name="defaultValue">The value the field has when the user sets none.</param>
/// <param name="edit">Applies a new value and returns the edit result.</param>
public abstract class SetupValueField<T>(
    SetupFieldDescription description, T value, T defaultValue, Func<T, SetupEditResult> edit)
    : SetupField(description)
{
    /// <summary>The current value.</summary>
    public T Value { get; } = value;
    /// <summary>The value the field has when the user sets none.</summary>
    public T DefaultValue { get; } = defaultValue;
    /// <summary>Applies <paramref name="value"/> through the edit callback, or returns an invalid result when the field is read-only, disabled or hidden.</summary>
    public SetupEditResult SetValue(T value) => Description.IsReadOnly || !Description.IsEnabled || !Description.IsVisible
        ? new(SetupEditState.Invalid, [new("field-unavailable", "This field cannot be edited.", Description.Id)])
        : edit(value);
}

/// <summary>A text field, optionally length-limited or backed by a path picker.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current text.</param>
/// <param name="defaultValue">The text the field has when the user sets none.</param>
/// <param name="edit">Applies a new text and returns the edit result.</param>
/// <param name="maximumLength">Most characters allowed, or null for no limit.</param>
/// <param name="pathSelection">The path picker the field offers, or null when the text is not a path.</param>
public sealed class SetupTextField(
    SetupFieldDescription description, string value, string defaultValue, Func<string, SetupEditResult> edit,
    int? maximumLength = null, SetupPathSelectionKind? pathSelection = null) : SetupValueField<string>(description, value, defaultValue, edit)
{
    /// <summary>Most characters allowed, or null for no limit.</summary>
    public int? MaximumLength { get; } = maximumLength;
    /// <summary>The path picker the field offers, or null when the text is not a path.</summary>
    public SetupPathSelectionKind? PathSelection { get; } = pathSelection;
}

/// <summary>A field that holds a URI, or null when none is set.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current URI.</param>
/// <param name="defaultValue">The URI the field has when the user sets none.</param>
/// <param name="edit">Applies a new URI and returns the edit result.</param>
public sealed class SetupUriField(
    SetupFieldDescription description, Uri? value, Uri? defaultValue, Func<Uri?, SetupEditResult> edit)
    : SetupValueField<Uri?>(description, value, defaultValue, edit);

/// <summary>A reference to an executable to run.</summary>
/// <param name="Value">The command name or path of the executable.</param>
public sealed record SetupExecutableReference(string Value);

/// <summary>A field that names an executable to run.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current executable.</param>
/// <param name="defaultValue">The executable the field has when the user sets none.</param>
/// <param name="edit">Applies a new executable and returns the edit result.</param>
public sealed class SetupExecutableField(
    SetupFieldDescription description, SetupExecutableReference value, SetupExecutableReference defaultValue,
    Func<SetupExecutableReference, SetupEditResult> edit)
    : SetupValueField<SetupExecutableReference>(description, value, defaultValue, edit);

/// <summary>An integer field with optional bounds.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current value, or null when unset.</param>
/// <param name="defaultValue">The value the field has when the user sets none.</param>
/// <param name="edit">Applies a new value and returns the edit result.</param>
/// <param name="minimum">Smallest allowed value, or null for no lower bound.</param>
/// <param name="maximum">Largest allowed value, or null for no upper bound.</param>
public sealed class SetupIntegerField(
    SetupFieldDescription description, long? value, long? defaultValue, Func<long?, SetupEditResult> edit,
    long? minimum = null, long? maximum = null)
    : SetupValueField<long?>(description, value, defaultValue, edit)
{
    /// <summary>Smallest allowed value, or null for no lower bound.</summary>
    public long? Minimum { get; } = minimum;
    /// <summary>Largest allowed value, or null for no upper bound.</summary>
    public long? Maximum { get; } = maximum;
}

/// <summary>A decimal field with optional bounds and a step.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current value, or null when unset.</param>
/// <param name="defaultValue">The value the field has when the user sets none.</param>
/// <param name="edit">Applies a new value and returns the edit result.</param>
/// <param name="minimum">Smallest allowed value, or null for no lower bound.</param>
/// <param name="maximum">Largest allowed value, or null for no upper bound.</param>
/// <param name="step">Increment between values the editor offers.</param>
public sealed class SetupDecimalField(
    SetupFieldDescription description, decimal? value, decimal? defaultValue, Func<decimal?, SetupEditResult> edit,
    decimal? minimum = null, decimal? maximum = null, decimal step = 1)
    : SetupValueField<decimal?>(description, value, defaultValue, edit)
{
    /// <summary>Smallest allowed value, or null for no lower bound.</summary>
    public decimal? Minimum { get; } = minimum;
    /// <summary>Largest allowed value, or null for no upper bound.</summary>
    public decimal? Maximum { get; } = maximum;
    /// <summary>Increment between values the editor offers.</summary>
    public decimal Step { get; } = step;
}

/// <summary>An on or off field.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="value">The current value.</param>
/// <param name="defaultValue">The value the field has when the user sets none.</param>
/// <param name="edit">Applies a new value and returns the edit result.</param>
public sealed class SetupBooleanField(
    SetupFieldDescription description, bool value, bool defaultValue, Func<bool, SetupEditResult> edit)
    : SetupValueField<bool>(description, value, defaultValue, edit);

/// <summary>How an edit changes a stored secret.</summary>
public enum SetupSecretEditKind { /** <summary>Leaves the stored secret unchanged.</summary> */ Keep, /** <summary>Stores a new secret.</summary> */ Replace, /** <summary>Removes the stored secret.</summary> */ Clear }

/// <summary>One-way secret input. Never place this value in diagnostics or logs.</summary>
public sealed class SetupSecretEdit
{
    private SetupSecretEdit(SetupSecretEditKind kind, string? value) { Kind = kind; Value = value; }
    /// <summary>The change this edit makes to the stored secret.</summary>
    public SetupSecretEditKind Kind { get; }
    /// <summary>The new secret for a replace edit, or null for keep and clear.</summary>
    public string? Value { get; }
    /// <summary>An edit that leaves the stored secret unchanged.</summary>
    public static SetupSecretEdit Keep { get; } = new(SetupSecretEditKind.Keep, null);
    /// <summary>An edit that removes the stored secret.</summary>
    public static SetupSecretEdit Clear { get; } = new(SetupSecretEditKind.Clear, null);
    /// <summary>Creates an edit that stores <paramref name="value"/> as the new secret.</summary>
    public static SetupSecretEdit Replace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SetupSecretEditKind.Replace, value);
    }

    /// <inheritdoc />
    public override string ToString() => Kind.ToString();
}

/// <summary>A field for a secret. It reports whether a secret is stored and never exposes the stored value.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="hasValue">True when a secret is stored.</param>
/// <param name="change">The pending change to the stored secret.</param>
/// <param name="edit">Applies a secret edit and returns the edit result.</param>
public sealed class SetupSecretField(
    SetupFieldDescription description, bool hasValue, SetupSecretEditKind change,
    Func<SetupSecretEdit, SetupEditResult> edit) : SetupField(description)
{
    /// <summary>True when a secret is stored.</summary>
    public bool HasValue { get; } = hasValue;
    /// <summary>The pending change to the stored secret.</summary>
    public SetupSecretEditKind Change { get; } = change;
    /// <summary>Applies <paramref name="value"/> through the edit callback, or returns an invalid result when the field is read-only, disabled or hidden.</summary>
    public SetupEditResult SetValue(SetupSecretEdit value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Description.IsReadOnly || !Description.IsEnabled || !Description.IsVisible
            ? new(SetupEditState.Invalid, [new("field-unavailable", "This field cannot be edited.", Description.Id)])
            : edit(value);
    }
}

/// <summary>One option a choice field offers.</summary>
/// <param name="Value">The value stored when the option is chosen.</param>
/// <param name="DisplayName">Label shown to the user.</param>
/// <param name="Description">Optional text that describes the option.</param>
public sealed record SetupChoice(string Value, string DisplayName, string? Description = null);
/// <summary>A request for one page of a choice field's options.</summary>
/// <param name="Search">Search text that filters the options; empty matches every option.</param>
/// <param name="Continuation">Token from the previous page's <see cref="SetupChoicePage.Continuation"/>, or null for the first page.</param>
/// <param name="PageSize">Most options to return.</param>
public sealed record SetupChoiceQuery(string Search = "", string? Continuation = null, int PageSize = 50);
/// <summary>Outcome of a choice query.</summary>
public enum SetupChoiceState { /** <summary>The query returned its page.</summary> */ Completed, /** <summary>The field is not an enabled, visible choice field.</summary> */ Unavailable, /** <summary>The choices could not be retrieved.</summary> */ Failed, /** <summary>The caller cancelled the query.</summary> */ Cancelled }
/// <summary>One page of a choice field's options.</summary>
/// <param name="Choices">The options on this page.</param>
/// <param name="Continuation">Token that requests the next page, or null on the last page.</param>
/// <param name="State">Outcome of the query.</param>
/// <param name="Issues">Problems found while querying, or null when there are none.</param>
public sealed record SetupChoicePage(
    IReadOnlyList<SetupChoice> Choices, string? Continuation = null,
    SetupChoiceState State = SetupChoiceState.Completed, IReadOnlyList<SetupIssue>? Issues = null);

/// <summary>Choice-source behavior, independent of any UI framework or manual-entry permission.</summary>
public sealed record SetupChoiceCapabilities(InferenceSupport SupportsSearch = InferenceSupport.Unknown, InferenceSupport SupportsPaging = InferenceSupport.Unknown, InferenceSupport SupportsRefresh = InferenceSupport.Unknown)
{
    /// <summary>Capabilities of a fixed option list with no search, paging or refresh.</summary>
    public static SetupChoiceCapabilities Bounded { get; } = new(InferenceSupport.Unsupported, InferenceSupport.Unsupported, InferenceSupport.Unsupported);
    /// <summary>Capabilities of a source that supports search, paging and refresh.</summary>
    public static SetupChoiceCapabilities Searchable { get; } = new(InferenceSupport.Supported, InferenceSupport.Supported, InferenceSupport.Supported);
}

/// <summary>A choice field. Its choices come from <see cref="IInferenceSettingsManager.QueryChoicesAsync"/>.</summary>
public sealed class SetupChoiceField(
    SetupFieldDescription description, string value, string defaultValue, Func<string, SetupEditResult> edit,
    bool allowsManualEntry,
    SetupSideEffect choiceSideEffects = SetupSideEffect.None,
    SetupChoiceCapabilities? capabilities = null)
    : SetupValueField<string>(description, value, defaultValue, edit)
{
    /// <summary>True when the user may enter a value that is not among the choices.</summary>
    public bool AllowsManualEntry { get; } = allowsManualEntry;
    /// <summary>Side effects of querying the choices, such as network use or potential billing.</summary>
    public SetupSideEffect ChoiceSideEffects { get; } = choiceSideEffects;
    /// <summary>What the choice source supports.</summary>
    public SetupChoiceCapabilities Capabilities { get; } = capabilities ?? SetupChoiceCapabilities.Bounded;
}

/// <summary>One row of a repeatable group.</summary>
/// <param name="Id">Identifier of the row within its group.</param>
/// <param name="Fields">The fields in the row.</param>
public sealed record SetupFieldRow(string Id, IReadOnlyList<SetupField> Fields);

/// <summary>A field that holds a variable number of rows of fields.</summary>
/// <param name="description">Identity and presentation state of the field.</param>
/// <param name="rows">The current rows.</param>
/// <param name="add">Adds a row and returns the edit result.</param>
/// <param name="remove">Removes the row with the given identifier and returns the edit result.</param>
public sealed class SetupRepeatableGroupField(
    SetupFieldDescription description, IReadOnlyList<SetupFieldRow> rows,
    Func<SetupEditResult> add, Func<string, SetupEditResult> remove) : SetupField(description)
{
    /// <summary>The current rows.</summary>
    public IReadOnlyList<SetupFieldRow> Rows { get; } = rows;
    /// <summary>Adds a row, or returns an invalid result when the group is read-only, disabled or hidden.</summary>
    public SetupEditResult Add() => CanEdit ? add() : Unavailable;
    /// <summary>Removes the row <paramref name="rowId"/>, or returns an invalid result when the group is read-only, disabled or hidden.</summary>
    public SetupEditResult Remove(string rowId) => CanEdit ? remove(rowId) : Unavailable;
    private bool CanEdit => !Description.IsReadOnly && Description.IsEnabled && Description.IsVisible;
    private SetupEditResult Unavailable => new(SetupEditState.Invalid,
        [new("field-unavailable", "This group cannot be edited.", Description.Id)]);
}
