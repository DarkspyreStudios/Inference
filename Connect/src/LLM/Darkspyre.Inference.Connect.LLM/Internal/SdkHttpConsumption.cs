using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class SdkHttpConsumption
{
    internal static Operation Enter(HttpClient client, CancellationToken token) => Enter((client as SdkHttpClientLease)?.GenerationOwner, token);
    internal static Operation Enter(IHttpClientFactory factory, CancellationToken token) => Enter(factory as SdkHttpGenerationPool, token);
    private static Operation Enter(SdkHttpGenerationPool? owner, CancellationToken token) => new(owner, owner?.EnterConsumption(token), token);

    internal static async Task<string> ReadAsStringAsync(HttpContent content, CancellationToken token = default)
    {
        using SdkHttpResponseLease.BodyOperation? operation = (content as SdkHttpResponseLease)?.Enter(token);
        string result = await content.ReadAsStringAsync(operation?.Token ?? token).ConfigureAwait(false);
        return Publish(content, result, operation?.Token ?? token);
    }
    internal static async Task<byte[]> ReadAsByteArrayAsync(HttpContent content, CancellationToken token = default)
    {
        using SdkHttpResponseLease.BodyOperation? operation = (content as SdkHttpResponseLease)?.Enter(token);
        byte[] result = await content.ReadAsByteArrayAsync(operation?.Token ?? token).ConfigureAwait(false);
        return Publish(content, result, operation?.Token ?? token);
    }
    internal static async Task<T?> ReadFromJsonAsync<T>(HttpContent content, JsonTypeInfo<T> metadata, CancellationToken token = default)
    {
        using SdkHttpResponseLease.BodyOperation? operation = (content as SdkHttpResponseLease)?.Enter(token);
        T? result = await content.ReadFromJsonAsync(metadata, operation?.Token ?? token).ConfigureAwait(false);
        return Publish(content, result, operation?.Token ?? token);
    }
    internal static async Task CopyToAsync(HttpContent content, Stream destination, CancellationToken token = default)
    {
        using SdkHttpResponseLease.BodyOperation? operation = (content as SdkHttpResponseLease)?.Enter(token);
        await content.CopyToAsync(destination, operation?.Token ?? token).ConfigureAwait(false);
        Fence(content, operation?.Token ?? token);
    }
    internal static async Task<Stream> ReadAsStreamAsync(HttpContent content, CancellationToken token = default)
    {
        using SdkHttpResponseLease.BodyOperation? operation = (content as SdkHttpResponseLease)?.Enter(token);
        Stream stream = await content.ReadAsStreamAsync(operation?.Token ?? token).ConfigureAwait(false);
        Fence(content, operation?.Token ?? token);
        return content is SdkHttpResponseLease owner && stream is not SdkHttpBodyStream ? new SdkHttpBodyStream(owner, stream) : stream;
    }
    internal static void Fence(HttpContent content, CancellationToken token) => (content as SdkHttpResponseLease)?.Fence(token);
    internal static T Publish<T>(HttpContent content, T value, CancellationToken token)
    { Fence(content, token); return value; }

    internal sealed class Operation(SdkHttpGenerationPool? owner, LlmOperationLifetime.Lease? lease, CancellationToken caller) : IDisposable
    {
        internal CancellationToken Token => lease?.Token ?? caller;
        internal void Fence() { if (lease is not null) owner!.Fence(Token); }
        internal T Publish<T>(T value) { Fence(); return value; }
        public void Dispose() => lease?.Dispose();
    }
}
