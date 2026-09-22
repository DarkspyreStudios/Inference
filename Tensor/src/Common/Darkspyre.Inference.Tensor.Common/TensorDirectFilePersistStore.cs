using Darkspyre.Persistence;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// Default filesystem persistence for tensor model packs. It exposes only the
/// application persistence contract; TensorSharp detects the returned
/// <see cref="FileStream"/> and uses its path internally when possible.
/// </summary>
public sealed class TensorDirectFilePersistStore : IPersistenceStore
{
    private const int CopyBufferSize = 1024 * 1024;
    private const string InternalDirectoryName = ".tensor-persistence";
    private readonly string _rootDirectory;

    public TensorDirectFilePersistStore()
        : this(GetDefaultRootDirectory())
    {
    }

    public TensorDirectFilePersistStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public Task<bool> ExistsAsync(
        string storeName,
        string assetName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolveAssetPath(ResolveStoreDirectory(storeName), assetName)));
    }

    public Task<Stream?> OpenReadAsync(
        string storeName,
        string assetName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string filePath = ResolveAssetPath(ResolveStoreDirectory(storeName), assetName);
        try
        {
            Stream content = new FileStream(
                filePath,
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

    public Task<IReadOnlyList<string>> EnumerateAsync(
        string storeName,
        string assetNamePrefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string storeDirectory = ResolveStoreDirectory(storeName);
        string prefix = ValidatePrefix(assetNamePrefix);
        if (!Directory.Exists(storeDirectory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
        };
        var names = new List<string>();
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(storeDirectory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string logicalName = Path.GetRelativePath(storeDirectory, filePath).Replace('\\', '/');
                if (logicalName.StartsWith(InternalDirectoryName + "/", StringComparison.OrdinalIgnoreCase)
                    || !logicalName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(logicalName);
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        names.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task SaveAsync(
        string storeName,
        string assetName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string storeDirectory = ResolveStoreDirectory(storeName);
        string destinationPath = ResolveAssetPath(storeDirectory, assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string stagingDirectory = Path.Combine(storeDirectory, InternalDirectoryName);
        Directory.CreateDirectory(stagingDirectory);
        string stagingPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.tmp");
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
            File.Move(stagingPath, destinationPath, overwrite: true);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    public Task DeleteAsync(
        string storeName,
        string assetName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolveAssetPath(ResolveStoreDirectory(storeName), assetName));
        return Task.CompletedTask;
    }

    private static string GetDefaultRootDirectory()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The operating system did not provide a per-user local application-data directory.");
        }

        return Path.Combine(localApplicationData, "Darkspyre Studios", "Inference", "Tensor");
    }

    private string ResolveStoreDirectory(string storeName)
    {
        ValidateStoreName(storeName);
        return Path.Combine(_rootDirectory, storeName);
    }

    private static string ResolveAssetPath(string storeDirectory, string assetName)
    {
        string logicalName = ValidateAssetName(assetName);
        string filePath = Path.GetFullPath(Path.Combine(
            storeDirectory,
            logicalName.Replace('/', Path.DirectorySeparatorChar)));
        string storePrefix = storeDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? storeDirectory
            : storeDirectory + Path.DirectorySeparatorChar;
        if (!filePath.StartsWith(storePrefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Asset '{assetName}' resolves outside its tensor store.");
        }

        return filePath;
    }

    private static void ValidateStoreName(string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        if (storeName is "." or ".."
            || storeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || storeName.Contains(Path.DirectorySeparatorChar)
            || storeName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A tensor persistence store name must be one portable path segment.", nameof(storeName));
        }
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

    private static string ValidatePrefix(string assetNamePrefix)
    {
        ArgumentNullException.ThrowIfNull(assetNamePrefix);
        if (assetNamePrefix.Length == 0)
        {
            return string.Empty;
        }

        string value = assetNamePrefix.EndsWith('/')
            ? assetNamePrefix[..^1]
            : assetNamePrefix;
        ValidateAssetName(value);
        return assetNamePrefix;
    }
}
