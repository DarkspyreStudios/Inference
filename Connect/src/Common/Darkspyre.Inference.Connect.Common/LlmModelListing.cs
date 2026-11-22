namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A public model-reservation holder reported by the provider.</summary>
/// <param name="Id">The public holder identity, not its private proof.</param>
/// <param name="Name">The holder's coordination name.</param>
public sealed record LlmModelHolder(string Id, string Name);

/// <summary>Actual resident and holder state, distinct from available model choice.</summary>
/// <param name="ModelId">The model's identity.</param>
/// <param name="Loaded">Whether the provider reports an actual resident.</param>
/// <param name="Holders">The public reservation holders.</param>
public sealed record LlmModelReservationState(string ModelId, bool Loaded, IReadOnlyList<LlmModelHolder> Holders);

/// <summary>One adapter listing and its explicit model-reservation capability and state.</summary>
/// <param name="Models">The available model choices.</param>
/// <param name="SupportsReservations">Whether this endpoint explicitly supports named reservations.</param>
/// <param name="ReservationModels">Public coordination state, including held models no longer available.</param>
public sealed record LlmModelListing(IReadOnlyList<LlmModel> Models, bool SupportsReservations,
    IReadOnlyList<LlmModelReservationState> ReservationModels);

/// <summary>A published immutable listing, including a failed attempt's diagnostic.</summary>
/// <param name="Version">The exact admitted connection version.</param>
/// <param name="Listed">Whether a listing attempt has completed.</param>
/// <param name="Listing">Successful facts, or null when the attempt failed.</param>
/// <param name="Diagnostic">The safe failure diagnostic, or null on success.</param>
public sealed record LlmPublishedModelList(string Version, bool Listed, LlmModelListing? Listing, string? Diagnostic);

/// <summary>The outcome of releasing the caller's own reservation.</summary>
/// <param name="ModelId">The model whose reservation was addressed.</param>
/// <param name="Released">Whether the caller no longer holds a reservation.</param>
/// <param name="Loaded">Whether the actual model remains resident.</param>
/// <param name="Holders">The public holders remaining after the operation.</param>
public sealed record LlmModelReleaseResult(string ModelId, bool Released, bool Loaded,
    IReadOnlyList<LlmModelHolder> Holders);
