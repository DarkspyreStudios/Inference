namespace Darkspyre.Inference.Common.Setup;

/// <summary>The install card's actions for a model or engine in a local store, as every self-host editor shows them.</summary>
public static class SetupInstallActions
{
    /// <summary>The action id that downloads the files.</summary>
    public const string Download = "download";

    /// <summary>The action id that deletes the files.</summary>
    public const string Remove = "remove";

    /// <summary>
    /// NotDownloaded: "Download (size)". FilesMissing: "Download again" and "Delete download". NotSupported:
    /// "Delete download". Installed: "Delete download", disabled with <paramref name="removeUnavailableReason"/> when it is set.
    /// </summary>
    /// <param name="state">The install state.</param>
    /// <param name="size">The total size in bytes, shown as <see cref="SetupByteSize.Format"/>.</param>
    /// <param name="canDownload">False disables the download actions.</param>
    /// <param name="removeUnavailableReason">Why an installed item cannot be deleted now, or null.</param>
    /// <returns>The card's actions in display order.</returns>
    public static IReadOnlyList<SetupActionDescriptor> For(SetupInstallState state, long size, bool canDownload = true,
        string? removeUnavailableReason = null) => state switch
        {
            SetupInstallState.NotDownloaded =>
            [
                new(Download, $"Download ({SetupByteSize.Format(size)})", SetupActionPurpose.Install,
                    SetupSideEffect.Network | SetupSideEffect.Download, IsEnabled: canDownload)
            ],
            SetupInstallState.FilesMissing =>
            [
                new(Download, "Download again", SetupActionPurpose.Install,
                    SetupSideEffect.Network | SetupSideEffect.Download, IsEnabled: canDownload),
                new(Remove, "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles, IsEnabled: true)
            ],
            SetupInstallState.NotSupported =>
            [
                new(Remove, "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles, IsEnabled: true)
            ],
            _ =>
            [
                new(Remove, "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles,
                    IsEnabled: removeUnavailableReason is null, UnavailableReason: removeUnavailableReason)
            ]
        };
}
