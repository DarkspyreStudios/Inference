namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Marks a client whose stored OAuth credential the facade renews after HTTP 401 before output.
/// The session's retry policy decides whether to resend.</summary>
internal interface ILlmUnauthorizedRenewal;
