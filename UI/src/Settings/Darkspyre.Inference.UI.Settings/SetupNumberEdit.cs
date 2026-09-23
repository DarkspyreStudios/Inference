namespace Darkspyre.Inference.UI.Settings;

/// <summary>A numeric control edit, including text that cannot yet become a typed provider value.</summary>
public sealed record SetupNumberEdit<T>(string Text, T? Value, string? Error) where T : struct;
