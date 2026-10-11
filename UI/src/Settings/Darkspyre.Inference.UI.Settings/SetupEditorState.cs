using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.UI.Settings;

/// <summary>Safe view state for host-owned Save and dirty-close controls. Contains no input values.</summary>
/// <param name="HasUnsavedChanges">True when the editor holds changes not yet saved.</param>
/// <param name="HasPendingInput">True when typed input has not been applied to the settings.</param>
/// <param name="HasInputErrors">True when any typed input has an error.</param>
/// <param name="CanSave">True when the host may save now.</param>
/// <param name="CommitOutcomeUncertain">True when it is unknown whether the last save took effect.</param>
public sealed record SetupEditorState(bool HasUnsavedChanges, bool HasPendingInput, bool HasInputErrors,
    bool CanSave, bool CommitOutcomeUncertain);

// Ephemeral editor-owned input, not another provider draft. Retained across wizard steps, never persisted.
internal sealed class SetupEditorInputState
{
    private readonly Dictionary<string, Input> _inputs = new(StringComparer.Ordinal);
    internal sealed class Input
    {
        public string Text { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public bool Initialized { get; set; }
        public bool Pending { get; set; }
        public IReadOnlyList<SetupIssue> Issues { get; set; } = [];
    }

    public Input Get(string id) => _inputs.TryGetValue(id, out Input? value) ? value : _inputs[id] = new();
    public bool HasPendingInput => _inputs.Values.Any(value => value.Pending);
    public IReadOnlyList<SetupIssue> Issues => _inputs.SelectMany(pair => pair.Value.Issues.Count > 0
        ? pair.Value.Issues : pair.Value.Pending
            ? [new SetupIssue("input-unapplied", "Apply or clear the pending input before saving.", pair.Key)]
            : Array.Empty<SetupIssue>()).ToArray();

    public void Reconcile(InferenceSettings settings)
    {
        var ids = Fields(settings).Select(field => field.Description.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in _inputs.Keys.Where(id => !ids.Contains(id)).ToArray()) { _inputs.Remove(id); }
    }

    public void Clear() => _inputs.Clear();

    public static IEnumerable<SetupField> Fields(InferenceSettings settings) =>
        settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).SelectMany(Flatten)
            .Concat(settings.Interaction is SetupBrowserAuthorization browser ? [browser.Callback] : []);

    public static IEnumerable<SetupField> Flatten(SetupField field)
    {
        yield return field;
        if (field is SetupRepeatableGroupField group)
        {
            foreach (SetupField child in group.Rows.SelectMany(row => row.Fields).SelectMany(Flatten)) { yield return child; }
        }
    }
}
