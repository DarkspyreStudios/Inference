namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Antigravity's use of the host's data folder: the host-tools service runs there. A release stops that service, so
/// every connection to it closes, and an open session reopens its host tools on its next turn.
/// </summary>
internal sealed class AntigravityDataFolderUser : ILlmDataFolderUser
{
    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default) =>
        await AntigravityHostTools.ReleaseAsync(cancellationToken).ConfigureAwait(false);
}
