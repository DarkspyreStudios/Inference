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
        XaiMediaData.RequireHttpUrl(url);
        using var consumption = SdkHttpConsumption.Enter(http, token);
        token = consumption.Token;
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        long bytes = 0;
        var errors = new List<Exception>();
        void Fence() { token.ThrowIfCancellationRequested(); fence(); consumption.Fence(); }
        try
        {
            Fence();
            request = new(HttpMethod.Get, new Uri(url));
            // No selected connection headers, authorization or cookies enter this pipeline.
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                headers.CancelAfter(timeout);
                dispatched();
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                observed(response);
            }
            Fence();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The unsigned media transfer failed.", null, response.StatusCode);
            Stream destination = await open(response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentLength, token).ConfigureAwait(false);
            Fence();
            // The response owns its content stream; only the response is disposed below.
            Stream source = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                Fence();
                int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                Fence();
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                bytes = checked(bytes + read);
                Fence();
            }
            if (response.Content.Headers.ContentLength is { } expected && bytes != expected)
                throw new HttpRequestException("The unsigned media transfer ended before its declared length.");
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (response is not null)
                try { response.Dispose(); }
                catch (Exception cleanup) { errors.Add(LlmResourcePreparation.AsCleanupFailure(cleanup, refusedOwner: response)); }
            if (request is not null)
                try { request.Dispose(); }
                catch (Exception cleanup) { errors.Add(LlmResourcePreparation.AsCleanupFailure(cleanup, refusedOwner: request)); }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw new AggregateException(errors);
        Fence();
        return bytes;
    }
}
