using System.Text.Json;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Reports the model packs an engine family can load from its store in the model store.</summary>
public static class TensorInstalledPacks
{
    private const string ManifestFileName = "install.json";
    private static readonly Uri FileOrigin = new("file:///");

    /// <summary>
    /// Returns every pack in <paramref name="storeName"/> of <paramref name="modelStore"/> whose install.json names
    /// artifacts that are all present at their recorded length, and that the family recognises: a built-in whose key and
    /// artifact hashes match, or a pack whose model.json matches its install.json, passes <paramref name="accepts"/> and,
    /// when a resolver is given, resolves as Supported from its installed files. Built-ins come first in their declared
    /// order, then the others by key. Reads no file content beyond the resolver's bounded header reads and hashes nothing.
    /// </summary>
    /// <param name="modelStore">The model store the host passes in.</param>
    /// <param name="storeName">The family's store key. A store without packs reports none.</param>
    /// <param name="builtIns">The family's built-in packs; empty for a family without built-ins.</param>
    /// <param name="accepts">The family's metadata check of a description; false or an ArgumentException refuses it.</param>
    /// <param name="formatCheck">The family's resolver, run over the installed files; null when the family has none.</param>
    /// <param name="cancellationToken">Cancels the report.</param>
    /// <gotcha>A file whose bytes changed but whose length did not is still reported. The lease before a load hashes every
    /// artifact and refuses it.</gotcha>
    public static async Task<IReadOnlyList<TensorModelPackDefinition>> ReportAsync(IPersist modelStore, string storeName,
        IReadOnlyList<TensorModelPackDefinition> builtIns, Func<TensorModelPackDefinition, bool> accepts,
        ITensorModelResolver? formatCheck, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentNullException.ThrowIfNull(builtIns);
        ArgumentNullException.ThrowIfNull(accepts);
        IReadOnlyList<string> names = await modelStore.EnumerateAsync(storeName, "packs/", cancellationToken).ConfigureAwait(false);
        var reportedBuiltIns = new HashSet<TensorModelKey>();
        var others = new List<TensorModelPackDefinition>();
        foreach (string name in names.Where(IsManifestName).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TensorInstalledPack? pack = await ReadPackAsync(modelStore, storeName, name, cancellationToken).ConfigureAwait(false);
                if (pack is null) { continue; }
                var key = new TensorModelKey(pack.Manifest.ModelId, pack.Manifest.Revision);
                TensorModelPackDefinition? builtIn = builtIns.FirstOrDefault(candidate => candidate.Key == key);
                if (builtIn is not null)
                {
                    // A built-in key never takes another manifest.
                    if (TensorModelPackManager.ManifestMatches(builtIn, pack.Manifest)) { reportedBuiltIns.Add(key); }
                    continue;
                }
                if (pack.Description is TensorModelPackDefinition description
                    && Accepts(accepts, description)
                    && (formatCheck is null || await ResolvesAsync(modelStore, storeName, description, formatCheck, cancellationToken).ConfigureAwait(false)))
                {
                    others.Add(description);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file removed or locked mid-report leaves that pack out of this report.
            }
        }

        return
        [
            .. builtIns.Where(builtIn => reportedBuiltIns.Contains(builtIn.Key)),
            .. others.OrderBy(definition => definition.Key.ModelId, StringComparer.Ordinal)
                .ThenBy(definition => definition.Key.Revision, StringComparer.Ordinal)
        ];
    }

    private static bool IsManifestName(string name)
    {
        string[] segments = name.Split('/');
        return segments.Length == 4 && segments[3] == ManifestFileName;
    }

    // The manifest and description of one pack whose artifacts are all present at their recorded length, or null.
    private static async Task<TensorInstalledPack?> ReadPackAsync(IPersist modelStore, string storeName, string name,
        CancellationToken cancellationToken)
    {
        PersistedModelManifest? manifest;
        await using (Stream? content = await modelStore.OpenReadAsync(storeName, name, cancellationToken).ConfigureAwait(false))
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
            foreach (PersistedArtifact artifact in manifest.Artifacts)
            {
                await using Stream? file = await modelStore.OpenReadAsync(storeName,
                    TensorModelPackManager.ArtifactName(key, artifact.RelativePath), cancellationToken).ConfigureAwait(false);
                if (file is null || !file.CanSeek || file.Length != artifact.Length) { return null; }
            }
        }
        catch (ArgumentException)
        {
            // A recorded path the model store cannot name does not parse as a manifest of this store.
            return null;
        }

        return new(manifest, await ReadDescriptionAsync(modelStore, storeName, key, manifest, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<TensorModelPackDefinition?> ReadDescriptionAsync(IPersist modelStore, string storeName,
        TensorModelKey key, PersistedModelManifest manifest, CancellationToken cancellationToken)
    {
        await using Stream? content = await modelStore.OpenReadAsync(storeName, TensorModelPackManager.DescriptionName(key),
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

    private static async Task<bool> ResolvesAsync(IPersist modelStore, string storeName, TensorModelPackDefinition description,
        ITensorModelResolver formatCheck, CancellationToken cancellationToken)
    {
        string primaryRole = formatCheck.Requirements[0].Role;
        TensorModelArtifactDefinition? primary = description.Artifacts.FirstOrDefault(artifact => artifact.Role == primaryRole);
        if (primary is null) { return false; }
        // The file URI names the asset and passes source validation; the reader ignores it.
        var candidate = new TensorModelCandidate("installed", new(null, null, primary.RelativePath), new(null, null, null),
            description.License, null, null,
            description.Artifacts.Select(artifact => new TensorSourceArtifact(artifact.RelativePath, artifact.Length, artifact.Sha256,
                new(FileOrigin, null, null, artifact.RelativePath, artifact.Provenance?.License ?? description.License),
                new Uri(FileOrigin, TensorModelPackManager.ArtifactName(description.Key, artifact.RelativePath)))).ToArray())
        {
            ArtifactRoles = description.Artifacts.ToDictionary(artifact => artifact.Role, artifact => artifact.RelativePath, StringComparer.Ordinal)
        };
        TensorModelResolution resolution = await formatCheck.ResolveCandidateAsync(candidate, TensorBackendKind.Auto,
            new TensorPersistArtifactReader(modelStore, storeName, description.Key), cancellationToken).ConfigureAwait(false);
        return resolution.Evaluation.State == TensorCompatibilityState.Supported;
    }
}

// One pack that passed the file check.
internal sealed record TensorInstalledPack(PersistedModelManifest Manifest, TensorModelPackDefinition? Description);

// Bounded reads of one pack's artifacts through the model store.
internal sealed class TensorPersistArtifactReader(IPersist modelStore, string storeName, TensorModelKey pack) : ITensorArtifactReader
{
    public async Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact,
        TensorMetadataRead read, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read.Offset < 0 || read.Length is < 1 or > 16 * 1024 * 1024 || read.Offset > long.MaxValue - read.Length
                || !Enum.IsDefined(read.Kind) || (read.Kind == TensorMetadataReadKind.CompleteMetadataFile
                    && (read.Offset != 0 || read.Length > 4 * 1024 * 1024)))
                throw new ArgumentException("Invalid metadata range.");
            await using Stream? file = await modelStore.OpenReadAsync(storeName,
                TensorModelPackManager.ArtifactName(pack, artifact.Path), cancellationToken).ConfigureAwait(false);
            if (file is null || !file.CanSeek || artifact.Length != file.Length || read.Offset > file.Length)
                throw new TensorSourceException(TensorSourceState.Unverified, "source-changed", "The installed artifact changed after its manifest was written.");
            file.Position = read.Offset;
            byte[] bytes = new byte[(int)Math.Min(read.Length, file.Length - read.Offset)];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read.Kind == TensorMetadataReadKind.CompleteMetadataFile && bytes.LongLength != file.Length)
                throw new TensorSourceException(TensorSourceState.Unverified, "metadata-size", "The metadata file exceeds its inspection bound.");
            return new(TensorSourceState.Available, new(bytes, read.Offset, file.Length));
        }
        catch (TensorSourceException exception) { return new(exception.State, Issues: [new(exception.Code, exception.Message)]); }
        catch (UnauthorizedAccessException) { return new(TensorSourceState.AccessDenied, Issues: [new("source-access", "The host does not have access to this location.")]); }
        catch (ArgumentException) { return new(TensorSourceState.InvalidRequest, Issues: [new("source-input", "The artifact location or inspection range is invalid.")]); }
        catch (IOException) { return new(TensorSourceState.Unverified, Issues: [new("source-read", "The artifact could not be inspected.")]); }
    }
}
