using System.Net;
using System.Net.Http.Headers;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>A sanitized source failure. Remote response bodies, URLs and credentials are never included.</summary>
public sealed class TensorSourceException(TensorSourceState state, string code, string message) : IOException(message)
{
    /// <summary>The state that classifies the failure, such as a transient failure or an invalid request.</summary>
    public TensorSourceState State { get; } = state;
    /// <summary>A stable code that names the failure, such as <c>source-timeout</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>Shared HTTP boundary for metadata and explicit installation. Caller-owned clients must disable
/// automatic redirects and carry no default credentials/headers; authorization comes only from the host callback.</summary>
internal sealed class TensorSourceHttp(HttpClient client, TensorSourceAccess? access)
{
    internal static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    internal async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token, RangeHeaderValue? range = null)
    {
        ValidateUri(uri);
        if (client.DefaultRequestHeaders.Any())
        {
            throw new TensorSourceException(TensorSourceState.InvalidRequest, "http-default-headers",
                "Use a dedicated source HTTP client without default headers or credentials.");
        }
        Uri origin = Origin(uri);
        TensorSourceCredential? credential = access is null ? null : await access(origin, token).ConfigureAwait(false);
        bool mayAuthorize = true;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = range;
            if (mayAuthorize && credential is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(credential.Scheme, credential.Parameter);
            }
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TensorSourceException(TensorSourceState.TransientFailure, "source-timeout", "The source request timed out.");
            }
            catch (HttpRequestException)
            {
                throw new TensorSourceException(TensorSourceState.TransientFailure, "source-network", "The source could not be reached.");
            }
            if (response.RequestMessage?.RequestUri is Uri actual && actual != uri)
            {
                response.Dispose();
                throw new TensorSourceException(TensorSourceState.InvalidRequest, "automatic-redirect",
                    "The source HTTP client must disable automatic redirects.");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 5)
                {
                    throw new TensorSourceException(TensorSourceState.Unverified, "source-redirect", "The source returned an invalid redirect chain.");
                }
                Uri next = new(uri, location);
                if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new TensorSourceException(TensorSourceState.InvalidRequest, "source-downgrade", "HTTPS source redirects cannot downgrade transport security.");
                }
                uri = next;
                ValidateUri(uri);
                // Once the chain leaves its original origin, never reattach that origin's credential.
                mayAuthorize &= Origin(uri) == origin;
                continue;
            }
            if (response.IsSuccessStatusCode) { return response; }
            TensorSourceException failure = Classify(response);
            response.Dispose();
            throw failure;
        }
        throw new InvalidOperationException("Unreachable redirect state.");
    }

    private static TensorSourceException Classify(HttpResponseMessage response)
    {
        bool gated = response.Headers.TryGetValues("X-Error-Code", out IEnumerable<string>? codes)
            && codes.Contains("GatedRepo", StringComparer.OrdinalIgnoreCase);
        TensorSourceState state = gated ? TensorSourceState.Gated : response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => TensorSourceState.AuthenticationRequired,
            HttpStatusCode.Forbidden => TensorSourceState.AccessDenied,
            HttpStatusCode.NotFound => TensorSourceState.NotFound,
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => TensorSourceState.TransientFailure,
            >= HttpStatusCode.InternalServerError => TensorSourceState.TransientFailure,
            _ => TensorSourceState.Unverified
        };
        return new(state, "source-" + state.ToString().ToLowerInvariant(), state switch
        {
            TensorSourceState.Gated => "The source requires model access or license acceptance.",
            TensorSourceState.AuthenticationRequired => "The source requires authentication.",
            TensorSourceState.AccessDenied => "The source denied access.",
            TensorSourceState.NotFound => "The source did not expose the requested item; it may be missing or private.",
            TensorSourceState.TransientFailure => "The source is temporarily unavailable.",
            _ => "The source returned an unsupported response."
        });
    }

    internal static Uri Origin(Uri uri) => new(uri.GetLeftPart(UriPartial.Authority) + "/");

    internal static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp
                && IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address))))
        {
            throw new TensorSourceException(TensorSourceState.InvalidRequest, "source-uri", "A source must use HTTPS or a loopback HTTP address without embedded credentials.");
        }
    }
}
