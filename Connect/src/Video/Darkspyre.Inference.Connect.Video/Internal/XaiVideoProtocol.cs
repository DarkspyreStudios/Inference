using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Video.Internal;

/// <summary>Stateless native mapping; media resolution and receipt authority remain with the admitted transport.</summary>
internal static class XaiVideoProtocol
{
    internal static void Validate(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = ValidateControls(request);
    }

    internal static (string Route, JsonObject Body) Build(VideoRequest request,
        Func<InferenceMediaSource, JsonObject> resolve, Action fence)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(fence);
        var controls = ValidateControls(request);
        var body = new JsonObject { ["model"] = request.ModelId };
        switch (request)
        {
            case VideoGenerationRequest generation:
                AddGeneration(body, generation, controls.Duration, resolve, fence);
                break;
            case VideoEditRequest edit:
                body["prompt"] = edit.Prompt;
                body["video"] = Resolve(edit.SourceVideo, resolve, fence, allowFile: true);
                break;
            case VideoExtensionRequest extension:
                body["prompt"] = extension.Prompt;
                body["video"] = Resolve(extension.SourceVideo, resolve, fence, allowFile: true);
                if (controls.Duration is { } duration) body["duration"] = duration;
                break;
        }
        if (controls.Storage is { } storage) body["storage_options"] = storage;
        if (controls.UploadUrl is { } uploadUrl) body["output"] = new JsonObject { ["upload_url"] = uploadUrl };
        if (controls.User is { } user) body["user"] = user;
        fence();
        return (controls.Route, body);
    }

    private static (string Route, int? Duration, JsonObject? Storage, string? UploadUrl, string? User)
        ValidateControls(VideoRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        if (request.ProviderOptions is not null and not XaiVideoOptions)
            throw new NotSupportedException("Unknown video provider controls.");
        string route;
        int? duration = null;
        switch (request)
        {
            case VideoGenerationRequest generation:
                route = "videos/generations";
                duration = ValidateGeneration(generation);
                break;
            case VideoEditRequest edit:
                route = "videos/edits";
                ArgumentException.ThrowIfNullOrWhiteSpace(edit.Prompt);
                ValidateSource(edit.SourceVideo, allowFile: true);
                break;
            case VideoExtensionRequest extension:
                route = "videos/extensions";
                // The primary schema requires prompt although the neutral contract permits unknown/omitted prompt.
                if (string.IsNullOrWhiteSpace(extension.Prompt))
                    throw new ArgumentException("This video extension route requires a prompt.", nameof(request));
                ValidateSource(extension.SourceVideo, allowFile: true);
                if (extension.ExtensionDuration is { } value) duration = WholeSeconds(value, 2, 10);
                break;
            default: throw new NotSupportedException("Unknown video operation.");
        }
        JsonObject? storage = null;
        string? uploadUrl = null;
        string? user = null;
        if (request.ProviderOptions is XaiVideoOptions options)
        {
            if (options.Storage is { } value) storage = XaiMediaData.Storage(value);
            if (options.Output is { } output)
            {
                if (!output.UploadUrl.IsAbsoluteUri) throw new ArgumentException("A video upload destination must be absolute.", nameof(request));
                XaiMediaData.RequireHttpUrl(output.UploadUrl.OriginalString);
                uploadUrl = output.UploadUrl.OriginalString;
            }
            if (options.User is { } valueUser)
            {
                if (request is VideoExtensionRequest) throw new NotSupportedException("Video extension has no user control mapping.");
                user = valueUser;
            }
        }
        return (route, duration, storage, uploadUrl, user);
    }

    private static int? ValidateGeneration(VideoGenerationRequest request)
    {
        if (request.FirstFrame is { } first) ValidateSource(first, allowFile: true);
        if (request.LastFrame is { } last) ValidateSource(last, allowFile: true);
        bool references = request.ReferenceImages is { Count: > 0 } || request.ReferenceAudio is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(request.Prompt) && request.FirstFrame is null && request.LastFrame is null
            && request.Keyframes is not { Count: > 0 } && !references)
            throw new ArgumentException("Text-only video generation requires a prompt.", nameof(request));
        int? duration = request.Duration is { } value ? WholeSeconds(value, 1, 15) : null;
        if (request.AspectRatio is { } aspect)
        {
            if (aspect is not ("1:1" or "16:9" or "9:16" or "4:3" or "3:4" or "3:2" or "2:3"))
                throw new ArgumentException("Unsupported video aspect ratio.", nameof(request));
        }
        if (request.ResolutionHeight is { } resolution)
        {
            if (resolution is not (480 or 720 or 1080)) throw new ArgumentOutOfRangeException(nameof(request));
            if (resolution == 1080 && (references || request.Keyframes is { Count: > 0 }
                || request.ModelId != "grok-imagine-video-1.5"))
                throw new NotSupportedException("The selected video mode has no documented native 1080p mapping.");
        }
        if (request.ModelId == "grok-imagine-video" && (request.LastFrame is not null
            || request.Keyframes is { Count: > 0 } || request.FirstFrame is not null && references))
            throw new NotSupportedException("This video model does not support the supplied frame combination.");
        if (request.Keyframes is { } keyframes)
        {
            if (keyframes.Count > 4) throw new ArgumentOutOfRangeException(nameof(request));
            foreach (VideoKeyframe frame in keyframes)
            {
                double seconds = frame.Timestamp.TotalSeconds;
                if (!double.IsFinite(seconds) || seconds <= 0 || request.Duration is { } total && frame.Timestamp >= total)
                    throw new ArgumentException("Invalid video keyframe timestamps.", nameof(request));
                ValidateSource(frame.Image, allowFile: true);
            }
        }
        if (request.ReferenceImages is { } images)
            foreach (InferenceMediaSource image in images) ValidateSource(image, allowFile: true);
        if (request.ReferenceAudio is { } audio)
        {
            if (audio.Count > 3) throw new ArgumentOutOfRangeException(nameof(request));
            foreach (VideoReferenceAudio source in audio)
            {
                switch (source)
                {
                    case VideoVoiceReference voice:
                        ArgumentException.ThrowIfNullOrWhiteSpace(voice.VoiceId);
                        break;
                    case VideoAudioReference supplied:
                        ValidateSource(supplied.Source, allowFile: false);
                        break;
                    default: throw new NotSupportedException("Unknown video reference audio.");
                }
            }
        }
        return duration;
    }

    private static void ValidateSource(InferenceMediaSource source, bool allowFile)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (source)
        {
            case BinaryMediaSource: break;
            case UrlMediaSource url:
                ArgumentNullException.ThrowIfNull(url.Url);
                XaiMediaData.RequireHttpUrl(url.Url.OriginalString);
                break;
            case FileMediaSource file:
                if (!allowFile) throw new NotSupportedException("Supplied video audio has no native file source mapping.");
                ArgumentNullException.ThrowIfNull(file.File);
                ArgumentException.ThrowIfNullOrWhiteSpace(file.File.FileId);
                break;
            case ResourceMediaSource resource:
                ArgumentNullException.ThrowIfNull(resource.Resource);
                break;
            default: throw new NotSupportedException("Unknown video reference source.");
        }
    }

    private static void AddGeneration(JsonObject body, VideoGenerationRequest request, int? duration,
        Func<InferenceMediaSource, JsonObject> resolve, Action fence)
    {
        if (request.Prompt is { } prompt) body["prompt"] = prompt;
        if (duration is { } seconds) body["duration"] = seconds;
        if (request.AspectRatio is { } aspect) body["aspect_ratio"] = aspect;
        if (request.ResolutionHeight is { } resolution) body["resolution"] = $"{resolution}p";
        if (request.FirstFrame is { } first) body["image"] = Resolve(first, resolve, fence, allowFile: true);
        if (request.LastFrame is { } last) body["last_frame"] = Resolve(last, resolve, fence, allowFile: true);
        if (request.Keyframes is { } keyframes)
        {
            var rows = new JsonArray();
            foreach (VideoKeyframe frame in keyframes)
            {
                fence();
                rows.Add(new JsonObject { ["image"] = Resolve(frame.Image, resolve, fence, allowFile: true), ["timestamp_s"] = frame.Timestamp.TotalSeconds });
            }
            body["keyframes"] = rows;
        }
        if (request.ReferenceImages is { } images)
        {
            var rows = new JsonArray();
            foreach (InferenceMediaSource image in images) rows.Add(Resolve(image, resolve, fence, allowFile: true));
            body["reference_images"] = rows;
        }
        if (request.ReferenceAudio is { } audio)
        {
            var rows = new JsonArray();
            foreach (VideoReferenceAudio source in audio)
            {
                fence();
                rows.Add(source is VideoVoiceReference voice
                    ? new JsonObject { ["voice_id"] = voice.VoiceId }
                    : Resolve(((VideoAudioReference)source).Source, resolve, fence, allowFile: false));
            }
            body["reference_audios"] = rows;
        }
        if (request.GenerateAudio is { } generateAudio) body["generate_audio"] = generateAudio;
    }

    private static int WholeSeconds(TimeSpan value, int minimum, int maximum)
    {
        if (value.Ticks % TimeSpan.TicksPerSecond != 0 || value < TimeSpan.FromSeconds(minimum)
            || value > TimeSpan.FromSeconds(maximum)) throw new ArgumentOutOfRangeException(nameof(value));
        return checked((int)(value.Ticks / TimeSpan.TicksPerSecond));
    }

    private static JsonObject Resolve(InferenceMediaSource source, Func<InferenceMediaSource, JsonObject> resolve,
        Action fence, bool allowFile)
    {
        ArgumentNullException.ThrowIfNull(source);
        fence();
        JsonObject resolved = resolve(source);
        fence();
        // Only the route's exact native source forms are admitted; this is not an arbitrary JSON bag.
        if (resolved.Count != 1 || resolved.First() is not { Key: "url" or "file_id", Value: JsonValue value }
            || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text)
            || !allowFile && resolved.ContainsKey("file_id"))
            throw new ArgumentException("A video reference has no supported resolved native source.", nameof(source));
        return (JsonObject)resolved.DeepClone();
    }

    internal static string ParseSubmission(string body, Func<string, bool> protectedValue)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw XaiMediaData.Malformed();
        return XaiMediaData.RequiredString(document.RootElement, "request_id", protectedValue);
    }

    internal static XaiVideoPoll ParsePoll(HttpStatusCode status, string? body,
        Func<string, bool> protectedValue, Action fence)
    {
        if (status == HttpStatusCode.Accepted && string.IsNullOrWhiteSpace(body))
            return new(VideoJobState.Pending, null, null, null, null, null, null);
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw XaiMediaData.Malformed();
        using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw XaiMediaData.Malformed();
        VideoJobState state = XaiMediaData.RequiredString(root, "status", protectedValue) switch
        {
            "pending" => VideoJobState.Pending,
            "done" => VideoJobState.Completed,
            "failed" => VideoJobState.Failed,
            "expired" => VideoJobState.Expired,
            _ => throw XaiMediaData.Malformed()
        };
        if (status == HttpStatusCode.Accepted && state != VideoJobState.Pending) throw XaiMediaData.Malformed();
        string? model = XaiMediaData.OptionalString(root, "model", protectedValue);
        int? progress = null;
        if (root.TryGetProperty("progress", out JsonElement progressValue) && progressValue.ValueKind != JsonValueKind.Null)
        {
            if (!progressValue.TryGetInt32(out int percent) || percent is < 0 or > 100) throw XaiMediaData.Malformed();
            progress = percent;
        }
        JsonElement? usage = XaiMediaData.Usage(root);
        string? errorCode = null;
        string? errorMessage = null;
        if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
        {
            if (error.ValueKind != JsonValueKind.Object) throw XaiMediaData.Malformed();
            errorCode = XaiMediaData.RequiredString(error, "code", protectedValue);
            // The adapter sanitizes diagnostic prose with its selected credential context.
            errorMessage = XaiMediaData.RequiredString(error, "message", static _ => false);
        }
        JsonElement? output = null;
        if (root.TryGetProperty("video", out JsonElement video) && video.ValueKind != JsonValueKind.Null)
        {
            if (video.ValueKind != JsonValueKind.Object || !video.TryGetProperty("duration", out JsonElement duration)
                || !duration.TryGetInt32(out int seconds) || seconds < 0
                || !video.TryGetProperty("respect_moderation", out JsonElement moderation)
                || moderation.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw XaiMediaData.Malformed();
            var observed = new JsonObject { ["duration"] = seconds, ["respect_moderation"] = moderation.GetBoolean() };
            string? url = XaiMediaData.OptionalString(video, "url", protectedValue);
            if (url is not null)
            {
                if (url.Length != 0 || moderation.GetBoolean()) XaiMediaData.RequireHttpUrl(url);
                observed["url"] = url;
            }
            JsonObject? file = XaiMediaData.FileOutput(video, protectedValue);
            if (file is not null) observed["file_output"] = file;
            if (XaiMediaData.OptionalString(video, "storage_error", protectedValue) is not null) observed["storage_failed"] = true;
            if (usage is { } observedUsage) observed["usage"] = JsonNode.Parse(observedUsage.GetRawText());
            output = XaiMediaData.Element(observed);
        }
        if (state == VideoJobState.Completed && output is null) throw XaiMediaData.Malformed();
        fence();
        return new(state, progress, model, output, usage, errorCode, errorMessage);
    }
}

internal sealed record XaiVideoPoll(VideoJobState State, int? ProgressPercent, string? ModelId,
    JsonElement? Output, JsonElement? Usage, string? ErrorCode, string? ErrorMessage)
{
    public override string ToString() => "XaiVideoPoll { <observation> }";
}
