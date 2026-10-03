using System.Globalization;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class XaiFileRequestData
{
    internal static void Connection(LlmConnection connection)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.BuildUri(connection, "files"));
        XaiRequestHeaders.Apply(request, connection, false);
    }

    internal static string Identity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.Any(char.IsControl))
            throw new ArgumentException("Invalid literal file identifier.", nameof(value));
        return value;
    }

    internal static string Path(string id) => "files/" + Uri.EscapeDataString(Identity(id));

    internal static long? Expiry(TimeSpan? value) => FileContractData.Expiry(value) is { } duration
        ? duration.Ticks / TimeSpan.TicksPerSecond : null;

    internal static long? Upload(Stream source, string fileName, LlmFileUploadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Any(char.IsControl)) throw new ArgumentException("The filename contains control characters.", nameof(fileName));
        if (!source.CanRead) throw new ArgumentException("Upload source must be readable.", nameof(source));
        return Expiry(options?.ExpiresAfter);
    }

    internal static string Download(string fileId, Stream destination, LlmFileDownloadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Download destination must be writable.", nameof(destination));
        return DownloadPath(fileId, options);
    }

    internal static string DownloadPath(string fileId, LlmFileDownloadOptions? options)
    {
        string path = Path(fileId) + "/content";
        if (options?.Format is { } format) path += "?format=" + (format switch
        {
            LlmFileContentFormat.Original => "original",
            LlmFileContentFormat.Text => "text",
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        });
        return path;
    }

    internal static string List(LlmFileListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int limit = FileContractData.Limit(request.Limit);
        var query = new List<KeyValuePair<string, string>> { new("limit", limit.ToString(CultureInfo.InvariantCulture)) };
        if (request.PaginationToken is { } token) query.Add(new("pagination_token", token));
        if (request.Order is { } order) query.Add(new("order", order switch
        {
            LlmFileOrder.Ascending => "asc",
            LlmFileOrder.Descending => "desc",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        }));
        if (request.Sort is { } sort) query.Add(new("sort_by", sort switch
        {
            LlmFileSort.CreatedAt => "created_at",
            LlmFileSort.FileName => "filename",
            LlmFileSort.Size => "size",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        }));
        if (request.Filter is { } filter) query.Add(new("filter", filter));
        return "files?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }
}
