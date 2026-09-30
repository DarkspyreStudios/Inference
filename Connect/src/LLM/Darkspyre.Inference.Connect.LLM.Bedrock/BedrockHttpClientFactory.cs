using System.Net;
using Amazon.Runtime;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockHttpClientFactory : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            AllowAutoRedirect = clientConfig.AllowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = clientConfig.ConnectTimeout ?? Timeout.InfiniteTimeSpan,
            MaxConnectionsPerServer = clientConfig.MaxConnectionsPerServer ?? int.MaxValue,
            Proxy = clientConfig is ClientConfig config ? config.GetWebProxy() : null
        };
        return new HttpClient(handler) { Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100) };
    }

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => true;
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
}
