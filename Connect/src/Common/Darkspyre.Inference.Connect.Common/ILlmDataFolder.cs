namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The host's folder for files Connect's providers keep: the host-tools service and CLI call scratch.</summary>
/// <gotcha>Every container that calls <see cref="ServiceCollectionExtensions.AddLlmConnect(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// registers one; a provider that keeps files there fails to resolve without it.</gotcha>
public interface ILlmDataFolder
{
    /// <summary>The absolute folder, read when called.</summary>
    ValueTask<string> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>A provider's use of <see cref="ILlmDataFolder"/> that must end before the folder moves.</summary>
public interface ILlmDataFolderUser
{
    /// <summary>Stops the provider's service in the folder, closing every connection to it, and returns once it has exited.</summary>
    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}
