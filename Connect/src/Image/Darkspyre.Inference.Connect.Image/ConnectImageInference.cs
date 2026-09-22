using System.Diagnostics;
using Common = Darkspyre.Inference.Common;
using CommonImage = Darkspyre.Inference.Common.Image;

namespace Darkspyre.Inference.Connect.Image;

internal sealed class ConnectImageInference(
    IImageTransport transport,
    ImageConnection connection,
    ImageWorkflow workflow,
    TimeSpan pollInterval) : CommonImage.IImageInference
{
    public Common.InferenceImplementationDescriptor Descriptor { get; } = new(
        "darkspyre.connect.image",
        "Darkspyre Connect Image",
        Common.InferenceImplementationKind.Connect);

    public Task<CommonImage.ImageCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (modelId is not null && !string.Equals(modelId, workflow.Id, StringComparison.Ordinal))
        {
            throw new Common.InferenceException(
                Common.InferenceFailureCode.ModelUnavailable,
                $"Workflow '{modelId}' is not bound to this image implementation.");
        }

        return Task.FromResult(new CommonImage.ImageCapabilities
        {
            SupportedSizes = [(workflow.DefaultWidth, workflow.DefaultHeight)],
            SupportsNegativePrompt = true,
            SupportsSeed = true,
            MaximumImageCount = 1
        });
    }

    public async Task<CommonImage.ImageGenerationResult> GenerateAsync(
        CommonImage.ImageGenerationRequest request,
        IProgress<CommonImage.ImageGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ModelId, workflow.Id, StringComparison.Ordinal))
        {
            throw new Common.InferenceException(
                Common.InferenceFailureCode.ModelUnavailable,
                $"Workflow '{request.ModelId}' is not bound to this image implementation.");
        }

        if (request.Count != 1)
        {
            throw new Common.InferenceException(
                Common.InferenceFailureCode.UnsupportedFeature,
                "Connect image generation currently accepts exactly one image per request.");
        }

        if (request.Steps is not null || request.GuidanceScale is not null)
        {
            throw new Common.InferenceException(
                Common.InferenceFailureCode.UnsupportedFeature,
                "The portable Connect image lane does not override workflow steps or guidance.");
        }

        int width = request.Width ?? workflow.DefaultWidth;
        int height = request.Height ?? workflow.DefaultHeight;
        long seed = request.Seed ?? Random.Shared.NextInt64();
        var prepared = transport.Prepare(new ImageTransportPreparationRequest
        {
            Workflow = workflow,
            PositivePrompt = request.Prompt,
            NegativePrompt = request.NegativePrompt ?? workflow.DefaultNegativePrompt,
            Width = width,
            Height = height,
            Seed = seed,
            CacheKey = Guid.NewGuid().ToString("N"),
            Label = workflow.Label
        });

        progress?.Report(new CommonImage.ImageGenerationProgress(CommonImage.ImageGenerationStage.Queued));
        string? jobId = null;
        long started = Stopwatch.GetTimestamp();
        try
        {
            jobId = await transport.SubmitAsync(connection, prepared, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                ImageTransportJobSnapshot snapshot = await transport.GetJobAsync(
                    connection,
                    jobId,
                    workflow,
                    cancellationToken).ConfigureAwait(false);
                switch (snapshot.State)
                {
                    case ImageTransportJobState.Queued:
                        progress?.Report(new CommonImage.ImageGenerationProgress(CommonImage.ImageGenerationStage.Queued));
                        break;
                    case ImageTransportJobState.Generating:
                        progress?.Report(new CommonImage.ImageGenerationProgress(CommonImage.ImageGenerationStage.Generating));
                        break;
                    case ImageTransportJobState.Completed when snapshot.Output is not null:
                        byte[] bytes = await transport.DownloadAsync(
                            connection,
                            snapshot.Output,
                            cancellationToken).ConfigureAwait(false);
                        progress?.Report(new CommonImage.ImageGenerationProgress(CommonImage.ImageGenerationStage.Completed));
                        return new CommonImage.ImageGenerationResult
                        {
                            Images =
                            [
                                new CommonImage.GeneratedImage
                                {
                                    ContentType = workflow.OutputContentType,
                                    Data = bytes,
                                    Seed = seed
                                }
                            ],
                            Provenance = new Common.InferenceProvenance
                            {
                                ImplementationId = Descriptor.Id,
                                ModelId = workflow.Id,
                                ModelRevision = workflow.Version,
                                Runtime = transport.Descriptor.DisplayName,
                                Backend = "connect",
                                Elapsed = Stopwatch.GetElapsedTime(started)
                            }
                        };
                    case ImageTransportJobState.Completed:
                        throw new Common.InferenceException(
                            Common.InferenceFailureCode.ExecutionFailed,
                            "The image provider completed without an output artifact.");
                    case ImageTransportJobState.Failed:
                        throw new Common.InferenceException(
                            Common.InferenceFailureCode.ExecutionFailed,
                            string.IsNullOrWhiteSpace(snapshot.Error)
                                ? "The image provider failed the generation job."
                                : snapshot.Error);
                    default:
                        throw new Common.InferenceException(
                            Common.InferenceFailureCode.ExecutionFailed,
                            $"The image provider returned unknown job state '{snapshot.State}'.");
                }

                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (jobId is not null)
        {
            await transport.TryCancelAsync(
                connection,
                jobId,
                interruptRunning: true,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
