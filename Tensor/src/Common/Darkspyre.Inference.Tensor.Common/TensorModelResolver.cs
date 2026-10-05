namespace Darkspyre.Inference.Tensor.Common;

/// <summary>One acquisition path for all families. Subclasses own compatibility, never transport or installation.</summary>
public abstract class TensorModelResolver : ITensorModelResolver
{
    protected TensorModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    { Source = source; Reader = reader ?? new TensorArtifactReader(source); }

    public ITensorModelSource? Source { get; }
    protected ITensorArtifactReader Reader { get; }
    public abstract IReadOnlyList<TensorArtifactRequirement> Requirements { get; }
    /// <summary>Families with split files accept explicitly selected additional artifacts under numbered roles.</summary>
    public virtual string? AdditionalArtifactRolePrefix => null;
    protected virtual string CompanionPath(string role, string path, string primary) => role + "/" + Path.GetFileName(path);
    public abstract Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default);

    public async Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default)
        => (await ResolveCandidateAsync(candidate, backend, Reader, cancellationToken).ConfigureAwait(false)).Evaluation;

    public async Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default)
    {
        TensorSourceResult<TensorModelCandidate> candidate = await DiscoverAsync(selection, cancellationToken).ConfigureAwait(false);
        return candidate.State == TensorSourceState.Available && candidate.Value is not null
            ? await ResolveCandidateAsync(candidate.Value, selection.Backend, Reader, cancellationToken).ConfigureAwait(false)
            : new(new(TensorCompatibilityState.Unverified, candidate.Issues ?? [], [], candidate.State));
    }

    /// <summary>Filters a listing to exactly the family-selected artifacts; unselected repository files are never imported.</summary>
    protected virtual IReadOnlyList<TensorSourceArtifact> SelectArtifacts(TensorModelCandidate candidate)
    {
        var paths = candidate.ArtifactRoles.Values.Append(candidate.Selection.Variant).ToHashSet(StringComparer.Ordinal);
        return candidate.Artifacts.Where(artifact => paths.Contains(artifact.Path)).ToArray();
    }

    public async Task<TensorSourceResult<TensorModelCandidate>> DiscoverAsync(TensorModelSelection selection, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(selection);
            cancellationToken.ThrowIfCancellationRequested();
            TensorModelCandidate candidate = await ReadLocationAsync(selection.Location, selection.License,
                selection.ExpectedLength, selection.ExpectedSha256, cancellationToken).ConfigureAwait(false);
            if (candidate.Artifacts.Count(item => item.Path == candidate.Selection.Variant) != 1)
                throw new TensorSourceException(TensorSourceState.Unverified, "primary-ambiguous", "The selected primary artifact is missing or ambiguous.");
            var artifacts = candidate.Artifacts.ToList();
            var roles = new Dictionary<string, string>(StringComparer.Ordinal) { [Requirements[0].Role] = candidate.Selection.Variant };
            TensorArtifactLocation[] overrides = selection.Companions?.ToArray() ?? [];
            if (overrides.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() != overrides.Length
                || overrides.Any(item => item.Role == Requirements[0].Role || !Requirements.Skip(1).Any(requirement => requirement.Role == item.Role)
                    && !(AdditionalArtifactRolePrefix is { } prefix && item.Role.StartsWith(prefix, StringComparison.Ordinal)
                        && int.TryParse(item.Role[prefix.Length..], out int number) && number is >= 2 and <= 256)))
                throw new ArgumentException("Companion roles must be unique and declared by the family.");
            var requirements = Requirements.Skip(1).Concat(overrides.Where(item => !Requirements.Any(requirement => requirement.Role == item.Role))
                .Select(item => new TensorArtifactRequirement(item.Role, string.Empty)));
            foreach (TensorArtifactRequirement requirement in requirements)
            {
                TensorArtifactLocation? input = overrides.SingleOrDefault(item => item.Role == requirement.Role);
                TensorSourceArtifact? artifact = null;
                if (input is null)
                {
                    TensorSourceArtifact[] matches = artifacts.Where(item => item.Path == requirement.FileName).ToArray();
                    if (matches.Length > 1) throw new ArgumentException("Ambiguous companion.");
                    artifact = matches.SingleOrDefault();
                    if (artifact is null) input = requirement.Default;
                }
                if (input is not null)
                {
                    TensorModelCandidate companion = await ReadLocationAsync(input.Location, input.License ?? selection.License ?? requirement.Default?.License,
                        input.ExpectedLength, input.ExpectedSha256, cancellationToken).ConfigureAwait(false);
                    artifact = companion.Artifacts.Single(item => item.Path == companion.Selection.Variant);
                    // Role-scoped names prevent unrelated sources' model.safetensors files from colliding.
                    artifact = artifact with { Path = CompanionPath(requirement.Role, artifact.Path, candidate.Selection.Variant) };
                    artifacts.Add(artifact);
                }
                if (artifact is null)
                    throw new TensorSourceException(TensorSourceState.Unverified, "companion-missing", $"Select a location for the required '{requirement.Role}' artifact.");
                roles.Add(requirement.Role, artifact.Path);
            }
            candidate = candidate with { Artifacts = artifacts, ArtifactRoles = roles };
            TensorSourceArtifact[] selected = SelectArtifacts(candidate).ToArray();
            if (selected.Length > 256 || selected.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != selected.Length)
                throw new TensorSourceException(TensorSourceState.Unverified, "artifacts-ambiguous", "The selected artifact set is too large or contains ambiguous paths.");
            return new(TensorSourceState.Available, candidate with { Artifacts = selected });
        }
        catch (TensorSourceException exception) { return new(exception.State, Issues: [new(exception.Code, exception.Message)]); }
        catch (UnauthorizedAccessException) { return new(TensorSourceState.AccessDenied, Issues: [new("source-access", "The host does not have access to the selected location.")]); }
        catch (DirectoryNotFoundException) { return new(TensorSourceState.NotFound, Issues: [new("source-missing", "The selected folder does not exist.")]); }
        catch (FileNotFoundException) { return new(TensorSourceState.NotFound, Issues: [new("source-missing", "The selected file does not exist.")]); }
        catch (ArgumentException) { return new(TensorSourceState.InvalidRequest, Issues: [new("source-input", "Check the location, primary artifact, companion roles, and expected integrity values.")]); }
        catch (IOException) { return new(TensorSourceState.Unverified, Issues: [new("source-read", "The selected location could not be inspected.")]); }
    }

    private async Task<TensorModelCandidate> ReadLocationAsync(TensorModelLocation location, string? license,
        long? expectedLength, string? expectedHash, CancellationToken token)
    {
        if (expectedLength < 0 || expectedHash is not null && (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid expected integrity.");
        if (location is TensorModelLocation.RepositoryArtifact repository)
        {
            if (Source is null || Source.Id != repository.SourceId)
                throw new TensorSourceException(TensorSourceState.InvalidRequest, "source-adapter", "The host has not supplied this repository adapter.");
            var result = await Source.GetCandidateAsync(repository.Selection, token).ConfigureAwait(false);
            if (result.State != TensorSourceState.Available || result.Value is null)
            {
                TensorSourceIssue? issue = result.Issues is { Count: > 0 } ? result.Issues[0] : null;
                throw new TensorSourceException(result.State, issue?.Code ?? "source-unavailable", issue?.Message ?? "The repository could not provide a candidate.");
            }
            TensorModelCandidate found = result.Value;
            TensorSourceArtifact ApplyMetadata(TensorSourceArtifact artifact)
            {
                if (artifact.Path == found.Selection.Variant)
                {
                    if (expectedLength is long size && artifact.Length is long actual && size != actual
                        || expectedHash is not null && artifact.Sha256 is not null && !string.Equals(expectedHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new TensorSourceException(TensorSourceState.Unverified, "source-changed", "Repository metadata disagrees with the expected artifact integrity.");
                    artifact = artifact with { Length = expectedLength ?? artifact.Length, Sha256 = expectedHash ?? artifact.Sha256 };
                }
                return artifact with { Provenance = artifact.Provenance with { License = artifact.Provenance.License ?? license } };
            }
            return found with
            {
                License = found.License ?? license,
                Artifacts = found.Artifacts.Select(ApplyMetadata).ToArray()
            };
        }
        var artifacts = new List<TensorSourceArtifact>();
        string primary;
        if (location is TensorModelLocation.LocalFolder folder)
        {
            string root = ValidateLocalPath(folder.Path);
            RejectLink(root);
            string[] files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Take(257).ToArray();
            if (files.Length > 256) throw new TensorSourceException(TensorSourceState.Unverified, "folder-bound", "Select a model folder containing at most 256 files.");
            primary = folder.PrimaryArtifact;
            if (string.IsNullOrWhiteSpace(primary))
            {
                string[] matches = files.Where(file => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(Requirements[0].FileName, Path.GetFileName(file))).ToArray();
                if (matches.Length != 1) throw new TensorSourceException(TensorSourceState.Unverified, "primary-ambiguous", "Specify the primary artifact filename; the folder does not contain exactly one matching model.");
                primary = Path.GetFileName(matches[0]);
            }
            TensorModelDefinitionValidator.ValidateRelativePath(primary);
            if (primary.Contains('/')) throw new ArgumentException("Select the containing folder directly.");
            foreach (string file in files)
            {
                RejectLink(file);
                artifacts.Add(LocalArtifact(file, license));
            }
            if (!artifacts.Any(item => item.Path == primary)) throw new FileNotFoundException();
        }
        else if (location is TensorModelLocation.LocalFile file)
        {
            string path = ValidateLocalPath(file.Path);
            RejectLink(path);
            TensorSourceArtifact artifact = LocalArtifact(path, license);
            artifacts.Add(artifact);
            primary = artifact.Path;
        }
        else if (location is TensorModelLocation.RemoteFile remote)
        {
            TensorSourceHttp.ValidateUri(remote.Url);
            // Durable acquisition locations never contain transient query credentials. Hosts supply origin-scoped authorization.
            if (remote.Url.Query.Length != 0) throw new ArgumentException("Use a durable URL without query credentials.");
            primary = Uri.UnescapeDataString(remote.Url.Segments.Last());
            TensorModelDefinitionValidator.ValidateRelativePath(primary);
            if (primary.Contains('/')) throw new ArgumentException("Invalid artifact name.");
            var artifact = new TensorSourceArtifact(primary, expectedLength, expectedHash,
                new(TensorSourceHttp.Origin(remote.Url), null, null, primary, license), remote.Url);
            if (expectedLength is null)
            {
                var probe = await Reader.ReadMetadataAsync(artifact, new(0, 1), token).ConfigureAwait(false);
                if (probe.Value is not null) artifact = artifact with { Length = probe.Value.TotalLength };
                else if (probe.State is not (TensorSourceState.Unverified or TensorSourceState.Available))
                {
                    TensorSourceIssue? issue = probe.Issues is { Count: > 0 } ? probe.Issues[0] : null;
                    throw new TensorSourceException(probe.State, issue?.Code ?? "source-unavailable", issue?.Message ?? "The URL is unavailable.");
                }
            }
            artifacts.Add(artifact);
        }
        else throw new ArgumentException("Unknown location type.");
        int index = artifacts.FindIndex(item => item.Path == primary);
        TensorSourceArtifact selected = artifacts[index];
        if (expectedLength is long length && selected.Length is long actual && actual != length)
            throw new TensorSourceException(TensorSourceState.Unverified, "source-changed", "The selected artifact does not match the expected size.");
        artifacts[index] = selected with { Sha256 = expectedHash, Length = expectedLength ?? selected.Length };
        return new(location is TensorModelLocation.RemoteFile ? "url" : "local", new(null, null, primary),
            new(null, null, null), license, null, null, artifacts);
    }

    private static string ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("An absolute local path is required.");
        return Path.GetFullPath(path);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new TensorSourceException(TensorSourceState.InvalidRequest, "source-link", "Select the real file or directory rather than a symbolic link.");
    }

    private static TensorSourceArtifact LocalArtifact(string path, string? license)
    {
        var file = new FileInfo(path);
        // URI serialization preserves OriginalString, which needs an explicit file scheme.
        Uri source = new(new Uri(file.FullName).AbsoluteUri, UriKind.Absolute);
        return new(file.Name, file.Length, null, new(new Uri("file:///"), null, null, file.Name, license), source);
    }
}
