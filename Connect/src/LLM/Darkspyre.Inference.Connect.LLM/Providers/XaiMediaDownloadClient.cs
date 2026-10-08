using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Unsigned output transfer through the selected core's SDK HTTP generation.</summary>
internal sealed class XaiMediaDownloadClient(HttpClient http)
{
    [SuppressMessage("Design", "CA1031", Justification = "The transfer retains the original failure and independent owned request/response cleanup failures.")]
    internal async Task<long> DownloadToAsync(string url, TimeSpan timeout,
        Func<string?, long?, CancellationToken, Task<Stream>> open, Action fence,
        Action dispatched, Action<HttpResponseMessage> observed, CancellationToken token)
    {
        XaiHttpDownload download = await OpenAsync(url, timeout, fence, token, dispatched, observed).ConfigureAwait(false);
        long bytes = 0;
        var errors = new List<Exception>();
        try
        {
            string? contentType = download.ContentType is { } type
                ? System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type).MediaType : null;
            Stream destination = await open(contentType, download.DeclaredLength, download.Token).ConfigureAwait(false);
            download.Fence();
            bytes = await download.CopyToAsync(destination).ConfigureAwait(false);
            if (download.DeclaredLength is { } expected && bytes != expected)
                throw new HttpRequestException("The unsigned media transfer ended before its declared length.");
        }
        catch (Exception error) { errors.Add(error); }
        try { await download.CloseAsync().ConfigureAwait(false); }
        catch (XaiHttpDownload.CloseFailure cleanup) { errors.AddRange(cleanup.InnerExceptions); }
        catch (Exception cleanup) { errors.Add(cleanup); }
        try
        {
            if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
            if (errors.Count > 1) throw new AggregateException(errors);
            download.Fence();
            return bytes;
        }
        finally { download.ReleaseAfterDrain(); }
    }

    internal Task<XaiHttpDownload> OpenAsync(string url, TimeSpan timeout, Action fence, CancellationToken token,
        Action? dispatched = null, Action<HttpResponseMessage>? observed = null)
    {
        XaiMediaData.RequireHttpUrl(url);
        // No selected connection headers, authorization or cookies enter this pipeline.
        return XaiHttpDownload.OpenAsync(http, new Uri(url), null, timeout, fence, (_, response, _) =>
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The unsigned media transfer failed.", null, response.StatusCode);
            return Task.CompletedTask;
        }, token, dispatched, observed);
    }
}
