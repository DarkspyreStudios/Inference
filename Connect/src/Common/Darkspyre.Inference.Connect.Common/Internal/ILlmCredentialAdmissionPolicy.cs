namespace Darkspyre.Inference.Connect.LLM.Internal;

// Selected adapters validate credentials without refreshing, saving or making provider calls.
internal interface ILlmCredentialAdmissionPolicy
{
    void ValidateCredential(LlmConnection connection);
}
