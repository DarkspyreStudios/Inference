using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AppleFoundationModelsPrototype;

// Only the C exports exercised by this research tool. Never expose these from Connect.
internal static partial class Native
{
    private const string Library = "DSA59AppleBridge";

    internal static void Load(string absolutePath)
    {
        nint library = NativeLibrary.Load(Path.GetFullPath(absolutePath));
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
            (string name, Assembly _, DllImportSearchPath? _) => name == Library ? library : 0);
        // A worker owns one load for its whole lifetime; callbacks must never enter unloaded code.
    }

    internal static string TakeString(nint value)
    {
        try { return Marshal.PtrToStringUTF8(value) ?? string.Empty; }
        finally { if (value != 0) FMFreeString(value); }
    }

    [LibraryImport(Library)]
    internal static partial nint FMSystemLanguageModelGetDefault();
    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool FMSystemLanguageModelIsAvailable(nint model, out int reason);
    [LibraryImport(Library)]
    internal static partial int FMSystemLanguageModelGetContextSize(nint model);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMLanguageModelSessionCreateFromSystemLanguageModel(
        nint model, string? instructions, nint tools, int toolCount);
    [LibraryImport(Library)]
    internal static partial nint FMLanguageModelSessionCreateFromTranscript(
        nint transcriptSession, nint model, nint tools, int toolCount);
    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool FMLanguageModelSessionIsResponding(nint session);
    [LibraryImport(Library)]
    internal static partial nint FMComposedPromptInitialize();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void FMComposedPromptAddText(nint prompt, string text);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMLanguageModelSessionRespond(
        nint session, nint prompt, string? options, nint userInfo, nint callback);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMLanguageModelSessionStreamResponse(nint session, nint prompt, string? options);
    [LibraryImport(Library)]
    internal static partial void FMLanguageModelSessionResponseStreamIterate(nint stream, nint userInfo, nint callback);
    [LibraryImport(Library)]
    internal static partial nint FMLanguageModelSessionGetTranscriptJSONString(nint session, out int code, out nint error);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMTranscriptCreateFromJSONString(string json, out int code, out nint error);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMGenerationSchemaCreate(string name, string? description);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMGenerationSchemaPropertyCreate(
        string name, string? description, string typeName, [MarshalAs(UnmanagedType.I1)] bool optional);
    [LibraryImport(Library)]
    internal static partial void FMGenerationSchemaAddProperty(nint schema, nint property);
    [LibraryImport(Library)]
    internal static partial void FMGenerationSchemaAddReferenceSchema(nint schema, nint reference);
    [LibraryImport(Library)]
    internal static partial void FMGenerationSchemaPropertyAddAnyOfGuide(
        nint property, nint choices, int count, [MarshalAs(UnmanagedType.I1)] bool wrapped);
    [LibraryImport(Library)]
    internal static partial void FMGenerationSchemaPropertyAddRangeGuide(
        nint property, double minimum, double maximum, [MarshalAs(UnmanagedType.I1)] bool wrapped);
    [LibraryImport(Library)]
    internal static partial void FMGenerationSchemaPropertyAddCountGuide(
        nint property, int count, [MarshalAs(UnmanagedType.I1)] bool wrapped);
    [LibraryImport(Library)]
    internal static partial nint FMGenerationSchemaGetJSONString(nint schema, out int code, out nint error);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMLanguageModelSessionRespondWithSchema(
        nint session, nint prompt, nint schema, string? options, nint userInfo, nint callback);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMLanguageModelSessionRespondWithSchemaFromJSON(
        nint session, nint prompt, string schema, string? options, nint userInfo, nint callback);
    [LibraryImport(Library)]
    internal static partial nint FMGeneratedContentGetJSONString(nint content);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FMBridgedToolCreate(
        string name, string description, nint schema, nint callback, out int code, out nint error);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void FMBridgedToolFinishCall(nint tool, uint callId, string output);
    [LibraryImport(Library)]
    internal static partial nint FMSystemLanguageModelTokenCountForPrompt(nint model, nint prompt, nint userInfo, nint callback);
    [LibraryImport(Library)]
    internal static partial void FMTaskCancel(nint task);
    [LibraryImport(Library)]
    internal static partial void FMRelease(nint value);
    [LibraryImport(Library)]
    internal static partial void FMFreeString(nint value);
}

internal sealed class NativeReference : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeReference(nint pointer) : base(true)
    {
        if (pointer == 0) throw new InvalidOperationException("Native bridge returned a null reference.");
        SetHandle(pointer);
    }

    internal nint Pointer => DangerousGetHandle();

    protected override bool ReleaseHandle()
    {
        Native.FMRelease(handle);
        return true;
    }
}
