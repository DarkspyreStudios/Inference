namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// The local directory that holds installed model packs. Assets are addressed by root-relative
/// logical paths using '/'. Writes are staged in the directory's own staging folder, on the same volume,
/// and moved into place, so a reader never sees a partial file and nothing passes through another volume.
/// </summary>
internal sealed class TensorPackDirectory
{
    private const int CopyBufferSize = 1024 * 1024;
    private const string InternalDirectoryName = ".tensor-staging";
    private readonly string _rootDirectory;

    public TensorPackDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>The per-user default: LocalApplicationData/Darkspyre Studios/Inference/Tensor.</summary>
    public static string DefaultRootDirectory
    {
        get
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("The operating system did not provide a per-user local application-data directory.");
            }

            return Path.Combine(localApplicationData, "Darkspyre Studios", "Inference", "Tensor");
        }
    }

    /// <summary>Returns the absolute file path of an asset. The file need not exist.</summary>
    public string GetPath(string assetName)
    {
        string logicalName = ValidateAssetName(assetName);
        string filePath = Path.GetFullPath(Path.Combine(
            _rootDirectory,
            logicalName.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;
        if (!filePath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Asset '{assetName}' resolves outside its tensor pack directory.");
        }

        return filePath;
    }

    /// <summary>Opens an asset for asynchronous sequential reading, or returns null when it does not exist.</summary>
    public Task<Stream?> OpenReadAsync(string assetName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Stream content = new FileStream(
                GetPath(assetName),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream?>(content);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <summary>Returns a new file path in the staging folder. The caller writes it, then publishes or deletes it.</summary>
    public string CreateStagingPath()
    {
        string stagingDirectory = Path.Combine(_rootDirectory, InternalDirectoryName);
        Directory.CreateDirectory(stagingDirectory);
        return Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Moves a complete staged file into place as the named asset.</summary>
    public void Publish(string stagingPath, string assetName)
    {
        string destinationPath = GetPath(assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(stagingPath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Deletes staging files that a stopped process left behind. A file written within
    /// <paramref name="age"/> may belong to another process's download in progress, so it stays.
    /// </summary>
    public void RemoveAbandonedStaging(TimeSpan age)
    {
        var staging = new DirectoryInfo(Path.Combine(_rootDirectory, InternalDirectoryName));
        if (!staging.Exists) { return; }
        DateTime cutoff = DateTime.UtcNow - age;
        foreach (FileInfo file in staging.EnumerateFiles("*.tmp"))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff) { file.Delete(); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task SaveAsync(string assetName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string stagingPath = CreateStagingPath();
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                BufferSize = CopyBufferSize,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                Share = FileShare.None,
            };
            await using (var output = new FileStream(stagingPath, options))
            {
                await content.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Publish(stagingPath, assetName);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    public Task DeleteAsync(string assetName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetPath(assetName));
        return Task.CompletedTask;
    }

    private static string ValidateAssetName(string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        if (Path.IsPathRooted(assetName) || assetName.Contains('\\'))
        {
            throw new ArgumentException("A tensor asset name must be a root-relative logical path using '/'.", nameof(assetName));
        }

        string[] segments = assetName.Split('/');
        if (segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or "..")
            || string.Equals(segments[0], InternalDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A tensor asset name contains an invalid path segment.", nameof(assetName));
        }

        return assetName;
    }
}
