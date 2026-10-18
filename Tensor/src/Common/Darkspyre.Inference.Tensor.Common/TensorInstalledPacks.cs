using System.Text.Json;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>An installed pack the family recognises but no longer loads, and why.</summary>
/// <param name="Definition">The pack's description, as its model.json records it.</param>
/// <param name="Reason">Why this release of the engine no longer loads it.</param>
public sealed record TensorUnsupportedPack(TensorModelPackDefinition Definition, string Reason);

/// <summary>What one family found in its pack folder: the packs it can load and the packs it no longer supports.</summary>
/// <param name="Installed">The packs the family can load: built-ins first in their declared order, then the others by key.</param>
/// <param name="Unsupported">The installed packs whose format check reports a retired architecture, in manifest order.</param>
public sealed record TensorInstalledReport(IReadOnlyList<TensorModelPackDefinition> Installed,
    IReadOnlyList<TensorUnsupportedPack> Unsupported)
{
    /// <summary>A report with no packs.</summary>
    public static TensorInstalledReport Empty { get; } = new([], []);
}

/// <summary>Reports the model packs an engine family can load from its pack folder.</summary>
public static class TensorInstalledPacks
{
    /// <summary>The issue code a format check returns for a pack whose architecture this release no longer loads.</summary>
    public const string RetiredArchitectureCode = "architecture-retired";

    private static readonly Uri FileOrigin = new("file:///");

    /// <summary>
    /// Reads every pack in <paramref name="packs"/>'s folder whose install.json names artifacts that are all present at
    /// their recorded length. A built-in whose key and artifact hashes match is installed. Any other pack needs a
    /// model.json that matches its install.json. When <paramref name="formatCheck"/> is given, it runs over the installed
    /// files first: an Unsupported verdict with the issue code <see cref="RetiredArchitectureCode"/> puts the pack in
    /// <see cref="TensorInstalledReport.Unsupported"/> with the issue's message. Otherwise the pack is installed when it
    /// passes <paramref name="accepts"/> and the check, if any, is Supported. Reads no file content beyond the resolver's
    /// bounded header reads and hashes nothing.
    /// </summary>
    /// <param name="packs">The family's pack manager; its folder is the one the report reads.</param>
    /// <param name="builtIns">The family's built-in packs; empty for a family without built-ins.</param>
    /// <param name="accepts">The family's metadata check of a description; false or an ArgumentException refuses it.</param>
    /// <param name="formatCheck">The family's resolver, run over the installed files; null when the family has none.</param>
    /// <param name="cancellationToken">Cancels the report.</param>
    /// <gotcha>A file whose bytes changed but whose length did not is still reported. The lease before a load hashes every
    /// artifact and refuses it.</gotcha>
    public static async Task<TensorInstalledReport> ReportAsync(TensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> builtIns, Func<TensorModelPackDefinition, bool> accepts,
        ITensorModelResolver? formatCheck, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(builtIns);
        ArgumentNullException.ThrowIfNull(accepts);
        TensorPackDirectory directory = packs.Directory;
        var reportedBuiltIns = new HashSet<TensorModelKey>();
        var others = new List<TensorModelPackDefinition>();
        var unsupported = new List<TensorUnsupportedPack>();
        foreach (string name in directory.EnumerateManifestNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TensorInstalledPack? pack = await ReadPackAsync(directory, name, cancellationToken).ConfigureAwait(false);
                if (pack is null) { continue; }
                var key = new TensorModelKey(pack.Manifest.ModelId, pack.Manifest.Revision);
                TensorModelPackDefinition? builtIn = builtIns.FirstOrDefault(candidate => candidate.Key == key);
                if (builtIn is not null)
                {
                    // A built-in key never takes another manifest.
                    if (TensorModelPackManager.ManifestMatches(builtIn, pack.Manifest)) { reportedBuiltIns.Add(key); }
                    continue;
                }
                if (pack.Description is not TensorModelPackDefinition description) { continue; }
                TensorModelEvaluation? evaluation = formatCheck is null ? null
                    : await EvaluateAsync(directory, description, formatCheck, cancellationToken).ConfigureAwait(false);
                if (evaluation is { State: TensorCompatibilityState.Unsupported }
                    && evaluation.Issues.FirstOrDefault(issue => issue.Code == RetiredArchitectureCode) is TensorSourceIssue retired)
                {
                    unsupported.Add(new(description, retired.Message));
                    continue;
                }
                if (Accepts(accepts, description) && (evaluation is null || evaluation.State == TensorCompatibilityState.Supported))
                {
                    others.Add(description);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A file removed or locked mid-report, or a folder name no asset can have, leaves that pack out.
            }
        }

        return new(
        [
            .. builtIns.Where(builtIn => reportedBuiltIns.Contains(builtIn.Key)),
            .. others.OrderBy(definition => definition.Key.ModelId, StringComparer.Ordinal)
                .ThenBy(definition => definition.Key.Revision, StringComparer.Ordinal)
        ], unsupported);
    }

    /// <summary>
    /// The listing an engine family's editor shows for <paramref name="packs"/>'s folder. Offered models:
    /// <paramref name="builtIns"/> in their declared order, then every installed model of <paramref name="installed"/>
    /// that is not a built-in, in the order given, then its unsupported packs in report order. An installed model is
    /// Installed and an unsupported pack is NotSupported with its reason. Any other model is FilesMissing when the folder
    /// holds a readable install.json at its key that names an artifact absent or not at its recorded length, and
    /// NotDownloaded otherwise. <paramref name="saved"/>, when given and not offered, becomes
    /// <see cref="TensorModelListing.Saved"/> with its state found the same way.
    /// </summary>
    /// <param name="packs">The family's pack manager.</param>
    /// <param name="store">The model store folder as the host's setting names it; copied into the listing.</param>
    /// <param name="builtIns">The family's built-in packs; empty for a family without built-ins.</param>
    /// <param name="installed">The family's last report of <paramref name="packs"/> (<see cref="ReportAsync"/>).</param>
    /// <param name="saved">The edited engine's saved model, or null for a new engine.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <gotcha>A pack whose install.json exists and whose files are all present, but which the report refused (a hash
    /// that differs from a built-in's, or a failed format check), is NotDownloaded. Its Download replaces it.</gotcha>
    public static async Task<TensorModelListing> ListAsync(TensorModelPackManager packs, string store,
        IReadOnlyList<TensorModelPackDefinition> builtIns, TensorInstalledReport installed,
        TensorModelPackDefinition? saved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(builtIns);
        ArgumentNullException.ThrowIfNull(installed);
        TensorPackDirectory directory = packs.Directory;
        var installedKeys = installed.Installed.Select(definition => definition.Key).ToHashSet();
        var builtInKeys = builtIns.Select(definition => definition.Key).ToHashSet();
        TensorModelPackDefinition[] offered = [.. builtIns, .. installed.Installed.Where(definition => !builtInKeys.Contains(definition.Key))];
        var models = new List<TensorListedModel>(offered.Length + installed.Unsupported.Count);
        foreach (TensorModelPackDefinition definition in offered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            models.Add(installedKeys.Contains(definition.Key)
                ? new(definition, SetupInstallState.Installed, null, null)
                : await CheckAsync(directory, definition, cancellationToken).ConfigureAwait(false));
        }
        models.AddRange(installed.Unsupported.Select(pack =>
            new TensorListedModel(pack.Definition, SetupInstallState.NotSupported, null, pack.Reason)));

        TensorListedModel? savedModel = saved is not null && !models.Any(model => model.Definition.Key == saved.Key)
            ? await CheckAsync(directory, saved, cancellationToken).ConfigureAwait(false)
            : null;
        return new(store, models, savedModel);
    }

    // The manifest and description of one pack whose artifacts are all present at their recorded length, or null.
    private static async Task<TensorInstalledPack?> ReadPackAsync(TensorPackDirectory directory, string name,
        CancellationToken cancellationToken)
    {
        PersistedModelManifest? manifest;
        await using (Stream? content = await directory.OpenReadAsync(name, cancellationToken).ConfigureAwait(false))
        {
            if (content is null) { return null; }
            try
            {
                manifest = await JsonSerializer.DeserializeAsync(content, TensorJsonContext.Default.PersistedModelManifest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) { return null; }
        }

        if (manifest is not { ModelId: not null, Revision: not null, Artifacts: not null }
            || manifest.Artifacts.Any(artifact => artifact?.RelativePath is null))
        {
            return null;
        }

        var key = new TensorModelKey(manifest.ModelId, manifest.Revision);
        try
        {
            if (TensorModelPackManager.ManifestName(key) != name) { return null; }
            if (await FirstMissingAsync(directory, key, manifest, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            // A recorded path the pack folder cannot name does not parse as a manifest of this folder.
            return null;
        }

        return new(manifest, await ReadDescriptionAsync(directory, key, manifest, cancellationToken).ConfigureAwait(false));
    }

    // The relative path of the first artifact of `manifest` that is absent or not at its recorded length, or null when
    // every one is present.
    private static async Task<string?> FirstMissingAsync(TensorPackDirectory directory, TensorModelKey key,
        PersistedModelManifest manifest, CancellationToken cancellationToken)
    {
        foreach (PersistedArtifact artifact in manifest.Artifacts)
        {
            await using Stream? file = await directory.OpenReadAsync(
                TensorModelPackManager.ArtifactName(key, artifact.RelativePath), cancellationToken).ConfigureAwait(false);
            if (file is null || file.Length != artifact.Length) { return artifact.RelativePath; }
        }

        return null;
    }

    // The state of one model that the report does not list: FilesMissing when its install.json names an artifact that
    // is absent or not at its recorded length, otherwise NotDownloaded.
    private static async Task<TensorListedModel> CheckAsync(TensorPackDirectory directory,
        TensorModelPackDefinition definition, CancellationToken cancellationToken)
    {
        var notDownloaded = new TensorListedModel(definition, SetupInstallState.NotDownloaded, null, null);
        try
        {
            PersistedModelManifest? manifest;
            await using (Stream? content = await directory.OpenReadAsync(
                TensorModelPackManager.ManifestName(definition.Key), cancellationToken).ConfigureAwait(false))
            {
                if (content is null) { return notDownloaded; }
                try
                {
                    manifest = await JsonSerializer.DeserializeAsync(content, TensorJsonContext.Default.PersistedModelManifest,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException) { return notDownloaded; }
            }

            if (manifest is not { ModelId: not null, Revision: not null, Artifacts: not null }
                || manifest.Artifacts.Any(artifact => artifact?.RelativePath is null)
                || new TensorModelKey(manifest.ModelId, manifest.Revision) != definition.Key)
            {
                return notDownloaded;
            }

            string? missing = await FirstMissingAsync(directory, definition.Key, manifest, cancellationToken).ConfigureAwait(false);
            return missing is null ? notDownloaded : new(definition, SetupInstallState.FilesMissing, missing, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A file removed, locked or unnamable mid-check leaves the model NotDownloaded, as the report leaves it out.
            return notDownloaded;
        }
    }

    private static async Task<TensorModelPackDefinition?> ReadDescriptionAsync(TensorPackDirectory directory,
        TensorModelKey key, PersistedModelManifest manifest, CancellationToken cancellationToken)
    {
        await using Stream? content = await directory.OpenReadAsync(TensorModelPackManager.DescriptionName(key),
            cancellationToken).ConfigureAwait(false);
        if (content is null) { return null; }
        try
        {
            TensorModelPackDefinition? description = await JsonSerializer.DeserializeAsync(content, TensorJson.ModelPack,
                cancellationToken).ConfigureAwait(false);
            if (description is null) { return null; }
            TensorModelDefinitionValidator.Validate(description);
            return TensorModelPackManager.ManifestMatches(description, manifest) ? description : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Accepts(Func<TensorModelPackDefinition, bool> accepts, TensorModelPackDefinition description)
    {
        try { return accepts(description); }
        catch (ArgumentException) { return false; }
    }

    // The format check's verdict over the installed files, read through file URIs into the pack folder.
    private static async Task<TensorModelEvaluation> EvaluateAsync(TensorPackDirectory directory,
        TensorModelPackDefinition description, ITensorModelResolver formatCheck, CancellationToken cancellationToken)
    {
        string primaryRole = formatCheck.Requirements[0].Role;
        TensorModelArtifactDefinition? primary = description.Artifacts.FirstOrDefault(artifact => artifact.Role == primaryRole);
        if (primary is null) { return new(TensorCompatibilityState.Unsupported, [], []); }
        var candidate = new TensorModelCandidate("installed", new(null, null, primary.RelativePath), new(null, null, null),
            description.License, null, null,
            description.Artifacts.Select(artifact => new TensorSourceArtifact(artifact.RelativePath, artifact.Length, artifact.Sha256,
                new(FileOrigin, null, null, artifact.RelativePath, artifact.Provenance?.License ?? description.License),
                new Uri(directory.GetPath(TensorModelPackManager.ArtifactName(description.Key, artifact.RelativePath))))).ToArray())
        {
            ArtifactRoles = description.Artifacts.ToDictionary(artifact => artifact.Role, artifact => artifact.RelativePath, StringComparer.Ordinal)
        };
        TensorModelResolution resolution = await formatCheck.ResolveCandidateAsync(candidate, TensorBackendKind.Auto,
            new TensorArtifactReader(), cancellationToken).ConfigureAwait(false);
        return resolution.Evaluation;
    }
}

// One pack that passed the file check.
internal sealed record TensorInstalledPack(PersistedModelManifest Manifest, TensorModelPackDefinition? Description);
