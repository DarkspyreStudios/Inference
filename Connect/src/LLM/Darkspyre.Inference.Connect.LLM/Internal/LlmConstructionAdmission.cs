namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmConstructionAdmission
{
    internal static T Read<T>(Action? admission, Func<T> read)
    {
        admission?.Invoke();
        T value = read();
        admission?.Invoke();
        return value;
    }
}
