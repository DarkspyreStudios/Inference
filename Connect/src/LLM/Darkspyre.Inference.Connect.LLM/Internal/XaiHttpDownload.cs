using System.Diagnostics.CodeAnalysis;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Owns one download response and its admitted HTTP generation. Its caller owns read admission and drain.</summary>
internal sealed class XaiHttpDownload
{
    private readonly Lock _gate = new();
    private SdkHttpConsumption.Operation? _consumption;
    private HttpRequestMessage? _request;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private Action? _fence;
    private Action<Exception>? _recordCleanup;
    private Action? _releaseAfterDrain;
    private Task? _closed;
    private int _released;

    private XaiHttpDownload(Action fence, Action<Exception>? recordCleanup, Action? releaseAfterDrain)
    {
        _fence = fence; _recordCleanup = recordCleanup; _releaseAfterDrain = releaseAfterDrain;
    }

    internal CancellationToken Token { get; private set; }
    internal string? ContentType { get; private set; }
    internal long? DeclaredLength { get; private set; }

    [SuppressMessage("Design", "CA1031", Justification = "Failed opening retains the original failure and each actual cleanup failure before releasing admitted owners.")]
    internal static async Task<XaiHttpDownload> OpenAsync(HttpClient http, Uri uri,
        Action<HttpRequestMessage>? configure, TimeSpan timeout, Action fence,
        Func<HttpResponseMessage, CancellationToken, Task> validate, CancellationToken token,
        Action? dispatched = null, Action<HttpResponseMessage>? observed = null,
        Action<Exception>? recordCleanup = null, Action? releaseAfterDrain = null)
    {
        var download = new XaiHttpDownload(fence, recordCleanup, releaseAfterDrain);
        try
        {
            download._consumption = SdkHttpConsumption.Enter(http, token);
            download.Token = download._consumption.Token;
            download.Fence();
            download._request = new(HttpMethod.Get, uri);
            configure?.Invoke(download._request);
            download.Fence();
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(download.Token))
            {
                headers.CancelAfter(timeout);
                dispatched?.Invoke();
                download._response = await http.SendAsync(download._request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            observed?.Invoke(download._response);
            download.Fence();
            await validate(download._response, download.Token).ConfigureAwait(false);
            download.Fence();
            download.ContentType = download._response.Content.Headers.ContentType?.ToString();
            download.DeclaredLength = download._response.Content.Headers.ContentLength;
            download.Fence();
            return download;
        }
        catch (Exception original)
        {
            try { await download.CloseAsync().ConfigureAwait(false); }
            catch (CloseFailure cleanup) { throw new OpenFailure([original, .. cleanup.InnerExceptions]); }
            catch (Exception cleanup) { throw new OpenFailure([original, cleanup]); }
            finally { download.ReleaseAfterDrain(); }
            throw;
        }
    }

    internal sealed class OpenFailure(IEnumerable<Exception> failures) : AggregateException(failures);
    internal sealed class CloseFailure(IEnumerable<Exception> failures) : AggregateException(failures);

    internal void Fence()
    {
        Token.ThrowIfCancellationRequested();
        _fence?.Invoke();
        _consumption?.Fence();
        Token.ThrowIfCancellationRequested();
    }

    internal async Task<Stream> OpenBodyAsync()
    {
        Fence();
        // Metadata preparation precedes this call. The response remains the body's sole disposal owner.
        _body = await SdkHttpConsumption.ReadAsStreamAsync(_response!.Content, Token).ConfigureAwait(false);
        Fence();
        return _body;
    }

    internal async Task<long> CopyToAsync(Stream destination)
    {
        Stream source = await OpenBodyAsync().ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        while (true)
        {
            Fence();
            int read = await source.ReadAsync(buffer, Token).ConfigureAwait(false);
            Fence();
            if (read == 0) return bytes;
            await destination.WriteAsync(buffer.AsMemory(0, read), Token).ConfigureAwait(false);
            bytes = checked(bytes + read);
            Fence();
        }
    }

    internal void RetainOwner(Action releaseAfterDrain, Action<Exception> recordCleanup)
    {
        _releaseAfterDrain += releaseAfterDrain;
        _recordCleanup += recordCleanup;
    }

    internal Task CloseAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closed is not null) return _closed;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closed = completion.Task;
        }
        Close(completion);
        return completion.Task;
    }

    [SuppressMessage("Design", "CA1031", Justification = "The owner closes each response/request once and retains independent actual cleanup refusals.")]
    private void Close(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        HttpResponseMessage? response = Interlocked.Exchange(ref _response, null);
        HttpRequestMessage? request = Interlocked.Exchange(ref _request, null);
        if (response is not null)
            try { response.Dispose(); }
            catch (Exception cleanup) { Record(cleanup, _body is null ? response : (response, _body)); }
        if (request is not null)
            try { request.Dispose(); }
            catch (Exception cleanup) { Record(cleanup, request); }
        if (failures.Count == 0) completion.TrySetResult();
        else if (failures.Count == 1) completion.TrySetException(failures[0]);
        else completion.TrySetException(new CloseFailure(failures));

        void Record(Exception cleanup, object owner)
        {
            Exception failure = LlmResourcePreparation.AsCleanupFailure(cleanup, refusedOwner: owner);
            failures.Add(failure);
            _recordCleanup?.Invoke(failure);
        }
    }

    // Escaped-stream owners call this only after closure and every admitted read have settled.
    internal void ReleaseAfterDrain()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _body = null;
        _fence = null;
        _recordCleanup = null;
        Action? release = _releaseAfterDrain;
        _releaseAfterDrain = null;
        SdkHttpConsumption.Operation? consumption = Interlocked.Exchange(ref _consumption, null);
        try { consumption?.Dispose(); }
        finally { release?.Invoke(); }
    }
}
