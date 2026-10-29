using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace MEAI_GPT_API.Service
{
    public class ImageGenerationOptions
    {
        public bool Enabled { get; set; }

        public string BaseUrl { get; set; } =
            "http://10.235.20.92:7457";

        public int TimeoutSeconds { get; set; } = 180;

        public int MaxPromptChars { get; set; } = 500;
    }

    public interface IImageGenerationService
    {
        bool IsEnabled { get; }

        Task<byte[]> GenerateAsync(
            string prompt,
            int width,
            int height,
            CancellationToken ct);
    }

    public class ImageGenerationService : IImageGenerationService
    {
        // The GPU handles one image at a time.
        private static readonly SemaphoreSlim _gate = new(1, 1);

        private readonly IHttpClientFactory _factory;
        private readonly IOptionsMonitor<ImageGenerationOptions> _opts;
        private readonly ILogger<ImageGenerationService> _log;

        public ImageGenerationService(
            IHttpClientFactory factory,
            IOptionsMonitor<ImageGenerationOptions> opts,
            ILogger<ImageGenerationService> log)
        {
            _factory = factory;
            _opts = opts;
            _log = log;
        }

        public bool IsEnabled => _opts.CurrentValue.Enabled;


private const string SafetyAndQualityPrefix = """
Generate professional, high-quality images.

SAFETY: Reject pornography, explicit sexual content,
sexualized minors, non-consensual intimate imagery,
and sexual deepfakes.

STRICT TEXT-FREE RULE:
Generate images containing NO text whatsoever.
Do not draw letters, words, numbers, symbols,
logos, captions, signs, labels, or watermarks.
Do not simulate handwriting, typography, or writing.
Keep posters, banners, screens, and central areas
completely free of text and lettering.
Create only the requested visual artwork.
If the request asks for text, represent its intended
meaning through visuals without drawing the text.

USER REQUEST:
""";

        public async Task<byte[]> GenerateAsync(
            string prompt,
            int width,
            int height,
            CancellationToken ct)
        {
            var o = _opts.CurrentValue;

            // Validate service configuration.
            if (!o.Enabled)
            {
                throw new InvalidOperationException(
                    "Image generation is disabled.");
            }

            if (string.IsNullOrWhiteSpace(o.BaseUrl))
            {
                throw new InvalidOperationException(
                    "Image generation BaseUrl is not configured.");
            }

            if (o.TimeoutSeconds <= 0)
            {
                throw new InvalidOperationException(
                    "Image generation TimeoutSeconds must be greater than zero.");
            }

            // Validate the original user prompt.
            if (string.IsNullOrWhiteSpace(prompt) ||
                prompt.Length > o.MaxPromptChars)
            {
                throw new ArgumentException(
                    $"Prompt must be 1-{o.MaxPromptChars} characters.",
                    nameof(prompt));
            }

            // FLUX.2 Klein server supports dimensions from 256 to 1280.
            // Validate before sending the request.
            if (width < 256 || width > 1280 ||
                height < 256 || height > 1280)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "Image width and height must be between 256 and 1280.");
            }

            // Prevent excessive waiting for the GPU.
            if (!await _gate.WaitAsync(
                    TimeSpan.FromSeconds(60),
                    ct))
            {
                throw new TimeoutException(
                    "Image generator is busy. Please try again shortly.");
            }

            try
            {
                using var cts =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);

                cts.CancelAfter(
                    TimeSpan.FromSeconds(o.TimeoutSeconds));

                var http = _factory.CreateClient("ImageGen");

                // Preserve the user request while adding safety and
                // image-quality instructions.
                //var safePrompt =
                //    $"{SafetyAndQualityPrefix}\n{prompt.Trim()}\n\n" +
                //    "Generate the requested image while following the " +
                //    "content-safety and image-quality instructions above.";

                string safePrompt = SafetyAndQualityPrefix + prompt.Trim();

                var endpoint = $"{o.BaseUrl.TrimEnd('/')}/generate";

                using var resp = await http.PostAsJsonAsync(
                    endpoint,
                    new
                    {
                        prompt = safePrompt.Trim(),
                        width,
                        height
                    },
                    cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(cts.Token);

                    _log.LogError(
                        "Image service returned HTTP {Status}. Response: {Body}",
                        (int)resp.StatusCode,
                        body);

                    throw new HttpRequestException(
                        $"Image service returned HTTP {(int)resp.StatusCode}: {body}");
                }

                var imageBytes =
                    await resp.Content.ReadAsByteArrayAsync(cts.Token);

                if (imageBytes.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Image service returned an empty response.");
                }

                _log.LogInformation(
                    "Image generated successfully. Size: {Size} bytes.",
                    imageBytes.Length);

                return imageBytes;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Image generation timed out after {TimeoutSeconds} seconds.",
                    o.TimeoutSeconds);

                throw new TimeoutException(
                    "Image generation timed out. Please try again.");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}