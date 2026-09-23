using Darkspyre.Inference.Common;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

internal static class WhisperPcm
{
    internal const int MinimumRate = 8000, MaximumRate = 192000, MaximumChannels = 8, TargetRate = 16000;

    internal static float[] Normalize(SpeechAudio audio, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(audio);
        token.ThrowIfCancellationRequested();
        if (audio.SampleRate is < MinimumRate or > MaximumRate || audio.Channels is < 1 or > MaximumChannels
            || audio.Samples.Length % audio.Channels != 0)
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "PCM requires complete interleaved frames, 1–8 channels and an 8–192 kHz sample rate.");
        var mono = new float[audio.Samples.Length / audio.Channels];
        ReadOnlySpan<float> samples = audio.Samples.Span;
        for (int frame = 0; frame < mono.Length; frame++)
        {
            if (frame % 4096 == 0) token.ThrowIfCancellationRequested();
            double sum = 0;
            for (int channel = 0; channel < audio.Channels; channel++)
            {
                float value = samples[frame * audio.Channels + channel];
                if (!float.IsFinite(value)) throw new InferenceException(InferenceFailureCode.InvalidRequest, "PCM samples must be finite.");
                sum += value;
            }
            mono[frame] = (float)(sum / audio.Channels);
        }
        if (mono.Length == 0 || audio.SampleRate == TargetRate) return mono;
        var resampler = new WdlResamplingSampleProvider(new SampleArray(mono, audio.SampleRate), TargetRate);
        var output = new List<float>();
        var buffer = new float[4096];
        int count;
        while ((count = resampler.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            for (int index = 0; index < count; index++)
            {
                if (!float.IsFinite(buffer[index])) throw new InferenceException(InferenceFailureCode.InvalidRequest, "PCM cannot be safely resampled.");
                output.Add(buffer[index]);
            }
        }
        return output.ToArray();
    }

    private sealed class SampleArray(float[] samples, int rate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int copied = Math.Min(count, samples.Length - _position);
            samples.AsSpan(_position, copied).CopyTo(buffer.AsSpan(offset, copied));
            _position += copied;
            return copied;
        }
    }
}
