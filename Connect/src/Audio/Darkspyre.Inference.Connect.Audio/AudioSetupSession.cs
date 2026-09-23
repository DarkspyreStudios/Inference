using System.Globalization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio;

public sealed record AudioSetupConfiguration(AudioConnection Connection)
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);
    public int? Steps { get; init; }
    public double? GuidanceScale { get; init; }
    public int? Seed { get; init; }
    public bool? UseCache { get; init; }
}

public sealed record AudioSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove);
public sealed record AudioSetupChanges(SetupConfigurationIdentity Identity, AudioSetupConfiguration Configuration, AudioSetupHeaderChanges Headers);

public sealed class AudioSetupSession : InferenceSetupSession<AudioSetupChanges>
{
    private readonly IAudioProvider _provider;
    private readonly SetupConfigurationIdentity _identity;
    private AudioSetupConfiguration _draft;
    private Uri? _endpoint;
    private decimal? _timeout;
    private decimal? _duration;
    private long? _steps;
    private long? _seed;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    internal AudioSetupSession(IAudioProvider provider, SetupConfigurationIdentity identity,
        ISetupCommitTarget<AudioSetupChanges> target, AudioSetupConfiguration? existing)
        : base(provider.SetupDefinition, target)
    {
        identity.Validate();
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != provider.Transport)
        { throw new ArgumentException("An edit requires the matching saved audio configuration.", nameof(existing)); }
        _identity = identity;
        _provider = provider;
        _draft = existing ?? new(new() { Transport = provider.Transport, Endpoint = new("http://127.0.0.1:8769") });
        _endpoint = _draft.Connection.Endpoint;
        _timeout = (decimal)_draft.Connection.RequestTimeout.TotalSeconds;
        _duration = (decimal)_draft.Duration.TotalSeconds;
        _steps = _draft.Steps;
        _seed = _draft.Seed;
        foreach ((string name, string value) in _draft.Connection.Headers) { _headers.Add(new(name, value)); }
        _draft = _draft with { Connection = _draft.Connection with { Headers = new Dictionary<string, string>() } };
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    protected override IReadOnlyList<SetupStep> CreateSteps(long revision) =>
    [
        new("connection", "External endpoint", "The host or administrator runs TangoFlux. Model listing and a separate health check are unavailable.",
        [new("connection", "Connection",
        [
            new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, new("http://127.0.0.1:8769"), value => Edit(revision, () => _endpoint = value)),
            new SetupDecimalField(new("timeout", "Timeout (seconds)") { IsRequired = true, HelpText = "A positive per-request timeout of at most one day." },
                _timeout, 12, value => Edit(revision, () => _timeout = value), 0, 86400)
        ]), new("options", "Endpoint options",
        [
            new SetupTextField(new("generate-path", "Generation path") { IsRequired = true }, _draft.Connection.TangoFlux.GeneratePath, "/generate",
                value => Edit(revision, () => _draft = _draft with { Connection = _draft.Connection with { TangoFlux = new() { GeneratePath = value } } })),
            Headers(revision)
        ], IsAdvanced: true)]),
        new("audio", "Audio defaults", "Saving does not generate audio. The explicit generation test can incur usage charges.",
        [new("generation", "Generation",
        [
            new SetupDecimalField(new("duration", "Duration (seconds)") { IsRequired = true }, _duration, 5, value => Edit(revision, () => _duration = value), 1, 30),
            new SetupIntegerField(new("steps", "Steps") { HelpText = "Blank uses the adapter default of 10." }, _steps, null,
                value => Edit(revision, () => _steps = value), 10, 100),
            new SetupDecimalField(new("guidance", "Guidance scale") { HelpText = "Blank uses the adapter default of 4.5." },
                _draft.GuidanceScale is { } guidance && double.IsFinite(guidance) && guidance > (double)decimal.MinValue && guidance < (double)decimal.MaxValue
                    ? (decimal)guidance : null, null,
                value => Edit(revision, () => _draft = _draft with { GuidanceScale = value is null ? null : (double)value }), 0.1m, 20),
            new SetupIntegerField(new("seed", "Seed") { HelpText = "Blank leaves seed selection to the server. Cache preference never changes the seed." },
                _seed, null, value => Edit(revision, () => _seed = value), int.MinValue, int.MaxValue),
            new SetupChoiceField(new("cache", "Provider cache") { HelpText = "Controls only the provider's cache, not an application cache." },
                CacheValue(_draft.UseCache), "default", value => value is "default" or "use" or "bypass"
                    ? Edit(revision, () => _draft = _draft with { UseCache = value == "default" ? null : value == "use" })
                    : new(SetupEditState.Invalid, [new("cache-invalid", "Select a supported cache preference.", "cache")]),
                false, (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(CacheChoices(query)), token), revision)
        ])])
    ];

    private static string CacheValue(bool? value) => value switch { true => "use", false => "bypass", _ => "default" };
    private static SetupChoicePage CacheChoices(SetupChoiceQuery query)
    {
        if (!int.TryParse(query.Continuation ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
        { return new(query.ExpectedRevision, [], State: SetupChoiceState.Failed); }
        SetupChoice[] choices = [new("default", "Server default"), new("use", "Allow provider cache"), new("bypass", "Bypass provider cache")];
        SetupChoice[] matches = choices.Where(choice => choice.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || choice.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(query.ExpectedRevision, matches.Skip(offset).Take(query.PageSize).ToArray(),
            query.PageSize < matches.Length - offset ? (offset + query.PageSize).ToString(CultureInfo.InvariantCulture) : null);
    }

    private SetupRepeatableGroupField Headers(long revision) => new(new("headers", "Protected HTTP headers"),
        _headers.Select(header => new SetupFieldRow(header.Id,
        [
            new SetupTextField(new(header.Id + ".name", "Name"), header.Name, string.Empty, value => Edit(revision, () => header.Name = value)),
            new SetupSecretField(new(header.Id + ".value", "Value"), header.Value is not null, header.Edit.Kind, value => Edit(revision, () => header.Edit = value))
        ])).ToArray(), () => Edit(revision, () => _headers.Add(new(string.Empty, null))),
        id => Edit(revision, () =>
        {
            Header? header = _headers.Find(item => item.Id == id);
            if (header is null) { return; }
            if (header.OriginalName.Length > 0) { _removed.Add(header.OriginalName); }
            _headers.Remove(header);
        }));

    protected override IReadOnlyList<ISetupAction> CreateActions(long revision) =>
    [
        BindAction(revision, new("test", "Generate a test clip", SetupActionPurpose.Test, SetupSideEffect.Network | SetupSideEffect.PotentialBilling,
            ValidateDraft().IsValid), async (_, token) =>
        {
            Publish(() => Status = Status with { Ready = SetupObservation.Unknown, Verified = SetupObservation.Unknown });
            AudioSetupConfiguration settings = Configuration();
            await _provider.Bind(settings.Connection).GenerateAsync(new()
            {
                Prompt = "A gentle breeze rustling leaves.", Duration = settings.Duration, Steps = settings.Steps,
                GuidanceScale = settings.GuidanceScale, Seed = settings.Seed, UseCache = settings.UseCache
            }, token).ConfigureAwait(false);
            Publish(() => Status = Status with { Ready = SetupObservation.Known(true), Verified = SetupObservation.Known(true) });
            return SetupActionResult.Completed;
        })
    ];

    private AudioConnection Connection() => _draft.Connection with
    {
        Endpoint = _endpoint!,
        RequestTimeout = TimeSpan.FromSeconds((double)(_timeout ?? 0)),
        Headers = _headers.Where(header => header.Value is not null).ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };

    private AudioSetupConfiguration Configuration() => _draft with
    {
        Connection = Connection(),
        Duration = TimeSpan.FromSeconds((double)_duration!),
        Steps = (int?)_steps,
        Seed = (int?)_seed
    };

    private bool ConnectionValid()
    {
        if (_endpoint is null || _timeout is null or <= 0 or > 86400
            || _headers.Any(header => string.IsNullOrWhiteSpace(header.Name))
            || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count) { return false; }
        try { TangoFluxHttpProvider.ValidateConnection(Connection()); return true; }
        catch (InferenceException) { return false; }
    }

    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (!ConnectionValid()) { issues.Add(new("connection-invalid", "Check the HTTP endpoint, path, timeout and protected headers.", StepId: "connection")); }
        if (_duration is null or < 1 or > 30) { issues.Add(new("duration-invalid", "Use a duration of 1–30 seconds.", "duration")); }
        if (_steps is < 10 or > 100) { issues.Add(new("steps-invalid", "Use 10–100 steps or leave blank for the default.", "steps")); }
        if (_draft.GuidanceScale is { } guidance && (!double.IsFinite(guidance) || guidance is < 0.1 or > 20))
        { issues.Add(new("guidance-invalid", "Use finite guidance of 0.1–20 or leave blank for the default.", "guidance")); }
        if (_seed is < int.MinValue or > int.MaxValue) { issues.Add(new("seed-invalid", "Use a 32-bit seed or leave blank for server selection.", "seed")); }
        return new(issues.AsReadOnly());
    }

    protected override AudioSetupChanges CreateChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        foreach (Header header in _headers)
        {
            bool renamed = !header.Name.Equals(header.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (header.OriginalName.Length > 0 && (renamed || header.Edit.Kind == SetupSecretEditKind.Clear)) { removed.Add(header.OriginalName); }
            if (header.Value is not null && (renamed || header.Edit.Kind == SetupSecretEditKind.Replace)) { set[header.Name] = header.Value; }
        }
        removed.ExceptWith(set.Keys);
        AudioSetupConfiguration settings = Configuration();
        return new(_identity, settings with { Connection = settings.Connection with { Headers = new Dictionary<string, string>() } }, new(set, removed.ToArray()));
    }

    protected override void ClearSensitiveState() => _headers.Clear();

    private sealed class Header(string name, string? originalValue)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = name;
        public string OriginalName { get; } = name;
        public SetupSecretEdit Edit { get; set; } = SetupSecretEdit.Keep;
        public string? Value => Edit.Kind switch { SetupSecretEditKind.Replace => Edit.Value, SetupSecretEditKind.Clear => null, _ => originalValue };
    }
}
