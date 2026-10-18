using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>One model an engine family's editor offers, with whether its files are in the model store.</summary>
/// <param name="Definition">The model.</param>
/// <param name="State">Whether its files are in the store.</param>
/// <param name="MissingFile">For <see cref="SetupInstallState.FilesMissing"/>, the relative path of the first absent or wrong-length file; otherwise null.</param>
/// <param name="UnsupportedReason">For <see cref="SetupInstallState.NotSupported"/>, why the engine no longer loads the model; otherwise null.</param>
public sealed record TensorListedModel(TensorModelPackDefinition Definition, SetupInstallState State, string? MissingFile,
    string? UnsupportedReason);

/// <summary>An engine family's offered models, the edited engine's own model when it is not among them, and the
/// model store folder they were checked in.</summary>
/// <param name="Store">The model store folder, as the host's setting names it.</param>
/// <param name="Models">The offered models: built-ins first in their declared order, then every other reported model by key,
/// then the installed models the engine no longer supports.</param>
/// <param name="Saved">The edited engine's saved model when it is not in <paramref name="Models"/>, with state FilesMissing
/// or NotDownloaded; otherwise null. It is never offered as a choice; the editor shows it only in the status card.</param>
public sealed record TensorModelListing(string Store, IReadOnlyList<TensorListedModel> Models, TensorListedModel? Saved);

/// <summary>
/// The host's current list of one engine family's models in the model store. A settings editor reads it when it
/// opens, after each <see cref="Changed"/> and after its own download or removal.
/// </summary>
public interface ITensorModelList
{
    /// <summary>Reads the listing as the host knows it now.</summary>
    Task<TensorModelListing> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after the host's list changes: a new report, a model store change, a download or a removal.</summary>
    event EventHandler? Changed;
}

/// <summary>Model lists that never change.</summary>
public static class TensorModelList
{
    /// <summary>A list that always returns <paramref name="listing"/> and never raises Changed. For hosts with no live model store, such as examples and the console.</summary>
    public static ITensorModelList Fixed(TensorModelListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return new FixedList(listing);
    }

    private sealed class FixedList(TensorModelListing listing) : ITensorModelList
    {
        public Task<TensorModelListing> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(listing);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
