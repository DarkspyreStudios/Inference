using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Image;
using Darkspyre.Inference.Tensor.STT;
using NAudio.Wave;

namespace GpuArbiter.Tensor;

public sealed class MediaApi(ITensorModelHost models, AdmissionGate admission, TimeProvider clock)
{
    public async Task ImagesAsync(HttpContext context, JsonElement root)
    {
        OpenAiInput.Fields(root, "model", "prompt", "n", "size", "response_format", "seed", "steps", "guidance_scale", "negative_prompt", "stream");
        var model = models.Get(OpenAiInput.String(root, "model"));
        if (model.Instance is not ITensorImageInference engine) throw new ApiException(400, "Selected model is not an image model", "model");
        var capabilities = await engine.GetCapabilitiesAsync(model.Pack.Key.ModelId, context.RequestAborted);
        var size = OpenAiInput.String(root, "size", false);
        var dimensions = capabilities.SupportedSizes.FirstOrDefault();
        if (size.Length > 0 && size != "auto")
        {
            var parts = size.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
                !capabilities.SupportedSizes.Contains((width, height))) throw new ApiException(400, "Unsupported image size", "size");
            dimensions = (width, height);
        }
        var format = OpenAiInput.String(root, "response_format", false);
        if (format.Length > 0 && format != "b64_json") throw new ApiException(400, "Only base64 image output is supported", "response_format");
        var count = OpenAiInput.Integer(root, "n") ?? 1;
        if (count > capabilities.MaximumImageCount) throw new ApiException(400, "Too many images", "n");
        long? seed = null;
        if (root.TryGetProperty("seed", out var seedValue))
        {
            if (!capabilities.SupportsSeed || seedValue.ValueKind != JsonValueKind.Number || !seedValue.TryGetInt64(out var parsed)) throw new ApiException(400, "Unsupported image seed", "seed");
            seed = parsed;
        }
        var steps = OpenAiInput.Integer(root, "steps");
        if (steps is not null && !capabilities.SupportsSteps) throw new ApiException(400, "Steps are not supported", "steps");
        double? guidance = null;
        if (root.TryGetProperty("guidance_scale", out var guidanceValue))
        {
            if (!capabilities.SupportsGuidanceScale || guidanceValue.ValueKind != JsonValueKind.Number || !guidanceValue.TryGetDouble(out var parsed) || !double.IsFinite(parsed) || parsed <= 0)
                throw new ApiException(400, "Unsupported guidance scale", "guidance_scale");
            guidance = parsed;
        }
        var negative = OpenAiInput.String(root, "negative_prompt", false);
        if (root.TryGetProperty("negative_prompt", out _) && !capabilities.SupportsNegativePrompt)
            throw new ApiException(400, "Negative prompts are not supported", "negative_prompt");
        bool stream = OpenAiInput.Boolean(root, "stream");
        var request = new ImageGenerationRequest
        {
            ModelId = model.Pack.Key.ModelId, Prompt = OpenAiInput.String(root, "prompt"),
            Width = dimensions.Width, Height = dimensions.Height, Count = count, Seed = seed,
            Steps = steps, GuidanceScale = guidance, NegativePrompt = negative.Length == 0 ? null : negative
        };
        using var lease = await admission.AcquireAsync(model.Alias, context, stream);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.HostStopped);
        var result = await engine.GenerateAsync(request, cancellationToken: execution.Token);
        var data = result.Images.Select(image => new { b64_json = Convert.ToBase64String(image.Data.Span) }).ToArray();
        var created = clock.GetUtcNow().ToUnixTimeSeconds();
        if (stream)
        {
            OpenAiOutput.BeginStream(context);
            for (int index = 0; index < data.Length; index++)
                await OpenAiOutput.SseAsync(context, new { type = "image_generation.completed", b64_json = data[index].b64_json, created_at = created, index }, execution.Token, "image_generation.completed");
        }
        else await context.Response.WriteAsJsonAsync(new { created, data }, execution.Token);
    }

    internal static SpeechAudio DecodeWave(Stream stream)
    {
        try
        {
            using var wave = new WaveFileReader(stream);
            if (wave.WaveFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
                throw new ApiException(400, "Only PCM or IEEE float WAV audio is supported", "file");
            if (wave.WaveFormat.Channels <= 0 || wave.WaveFormat.SampleRate <= 0) throw new ApiException(400, "Invalid WAV format", "file");
            var samples = wave.ToSampleProvider();
            var output = new List<float>();
            var block = new float[4096];
            int read;
            while ((read = samples.Read(block, 0, block.Length)) > 0)
            {
                if (output.Count + (long)read > 25 * 1024 * 1024) throw new ApiException(413, "Decoded audio is too large", "file");
                for (int index = 0; index < read; index++)
                {
                    if (!float.IsFinite(block[index])) throw new ApiException(400, "Audio contains non-finite samples", "file");
                    output.Add(block[index]);
                }
            }
            if (output.Count == 0 || output.Count % wave.WaveFormat.Channels != 0) throw new ApiException(400, "Audio must contain complete PCM frames", "file");
            return new(output.ToArray(), wave.WaveFormat.SampleRate, wave.WaveFormat.Channels);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or EndOfStreamException)
        { throw new ApiException(400, "Malformed or unsupported WAV audio", "file"); }
    }

    public async Task AudioAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType) throw new ApiException(400, "Expected multipart audio upload");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        foreach (var field in form.Keys)
            if (field is not ("model" or "language" or "response_format" or "timestamp_granularities[]") || form[field].Count != 1)
                throw new ApiException(400, "Unsupported or repeated transcription field", field);
        if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new ApiException(400, "One audio file is required", "file");
        var model = models.Get(form["model"].ToString());
        if (model.Instance is not ITensorSpeechToTextInference engine) throw new ApiException(400, "Selected model is not a transcription model", "model");
        var capabilities = await engine.GetCapabilitiesAsync(model.Pack.Key.ModelId, context.RequestAborted);
        bool translate = context.Request.Path == "/v1/audio/translations";
        if (translate && capabilities.SupportsTranslationToEnglish != true) throw new ApiException(400, "English translation is not supported", "model");
        var format = form["response_format"].ToString();
        if (format.Length == 0) format = "json";
        if (format is not ("json" or "text" or "verbose_json" or "srt" or "vtt")) throw new ApiException(400, "Unsupported transcription format", "response_format");
        var timestamps = form["timestamp_granularities[]"].ToString();
        if (timestamps.Length > 0 && (timestamps != "segment" || format != "verbose_json"))
            throw new ApiException(400, "Only segment timestamps in verbose_json are supported", "timestamp_granularities[]");
        using var upload = form.Files[0].OpenReadStream();
        var audio = DecodeWave(upload);
        if (audio.SampleRate < capabilities.MinimumSampleRate || audio.SampleRate > capabilities.MaximumSampleRate || audio.Channels > capabilities.MaximumChannels)
            throw new ApiException(400, "Audio sample rate or channels exceed model limits", "file");
        var language = form["language"].ToString();
        if (language.Length > 0 && capabilities.Languages is { } languages && !languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            throw new ApiException(400, "Unsupported language", "language");
        using var lease = await admission.AcquireAsync(model.Alias, context);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.HostStopped);
        var result = await engine.TranscribeAsync(new(audio, new(language.Length == 0 ? null : language, translate), model.Pack.Key.ModelId), execution.Token);
        if (format == "text")
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(result.Text, execution.Token);
        }
        else if (format is "srt" or "vtt")
        {
            context.Response.ContentType = format == "vtt" ? "text/vtt; charset=utf-8" : "application/x-subrip; charset=utf-8";
            string Timestamp(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{(format == "srt" ? ',' : '.')}{time.Milliseconds:000}";
            var text = format == "vtt" ? "WEBVTT\n\n" : "";
            foreach (var (segment, index) in result.Segments.Select((segment, index) => (segment, index)))
                text += $"{index + 1}\n{Timestamp(segment.Start)} --> {Timestamp(segment.End)}\n{segment.Text}\n\n";
            await context.Response.WriteAsync(text, execution.Token);
        }
        else if (format == "verbose_json") await context.Response.WriteAsJsonAsync(new
        {
            task = translate ? "translate" : "transcribe", language = result.DetectedLanguage,
            duration = audio.Samples.Length / (double)(audio.SampleRate * audio.Channels), text = result.Text,
            segments = result.Segments.Select((segment, index) => new { id = index, start = segment.Start.TotalSeconds, end = segment.End.TotalSeconds, text = segment.Text })
        }, execution.Token);
        else await context.Response.WriteAsJsonAsync(new { text = result.Text }, execution.Token);
    }
}
