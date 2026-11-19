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

    public class ImageComposeRequest
    {
        public string Scene { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? Footer { get; set; }
        public string Position { get; set; } = "bottom";
        public bool Panel { get; set; } = true;
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
    }

    public interface IImageGenerationService
    {
        bool IsEnabled { get; }

        Task<byte[]> GenerateAsync(
            string prompt,
            int width,
            int height,
            CancellationToken ct);
        Task<byte[]> ComposeAsync(ImageComposeRequest request, CancellationToken ct);
        Task<byte[]> NormalizeAsync(byte[] image, CancellationToken ct);
        Task<byte[]> EditAsync(byte[] image, string prompt, CancellationToken ct);
        Task<byte[]> OverlayAsync(byte[] image, string title, string? subtitle, string? footer, string position, CancellationToken ct);
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
        private const int MaxTotalPromptChars = 1000;
        public bool IsEnabled => _opts.CurrentValue.Enabled;


        private const string SafetyAndQualityPrefix = """
Generate professional, high-quality visual artwork.
SAFETY: Reject explicit sexual content, sexualized minors,
non-consensual intimate imagery, and sexual deepfakes.
USER REQUEST:
""";

        private string BuildSafePrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt cannot be empty.");

            string trimmedPrompt = prompt.Trim();

            int availableChars =
                MaxTotalPromptChars - SafetyAndQualityPrefix.Length - 1;

            if (trimmedPrompt.Length > availableChars)
            {
                throw new ArgumentException(
                    $"Prompt cannot exceed {availableChars} characters " +
                    "because safety instructions are included.");
            }

            return $"{SafetyAndQualityPrefix}\n{trimmedPrompt}";
        }

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

                const int maxTotalPromptChars = 1000;

                string safePrompt = BuildSafePrompt(prompt);

                if (safePrompt.Length > maxTotalPromptChars)
                {
                    throw new ArgumentException(
                        $"The complete prompt, including safety instructions, " +
                        $"must not exceed {maxTotalPromptChars} characters.");
                }

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

        public async Task<byte[]> ComposeAsync(
            ImageComposeRequest r,
            CancellationToken ct)
        {
            var o = _opts.CurrentValue;

            if (!o.Enabled)
                throw new InvalidOperationException(
                    "Image generation is disabled.");

            if (string.IsNullOrWhiteSpace(r.Scene) ||
                r.Scene.Length > o.MaxPromptChars)
            {
                throw new ArgumentException(
                    $"Scene description must be 1-{o.MaxPromptChars} characters.");
            }

            if (string.IsNullOrWhiteSpace(r.Title) ||
                r.Title.Length > 120)
            {
                throw new ArgumentException(
                    "Title is required (max 120 characters).");
            }

            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(60), ct))
            {
                throw new TimeoutException(
                    "Image generator is busy. Please try again shortly.");
            }

            try
            {
                using var cts =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);

                cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));

                var http = _factory.CreateClient("ImageGen");

                var url = $"{o.BaseUrl.TrimEnd('/')}/compose";

                var safeScene = BuildSafePrompt(r.Scene.Trim());

                var payload = new
                {
                    prompt = safeScene,
                    title = r.Title.Trim(),

                    subtitle = string.IsNullOrWhiteSpace(r.Subtitle)
                        ? null
                        : r.Subtitle.Trim(),

                    footer = string.IsNullOrWhiteSpace(r.Footer)
                        ? null
                        : r.Footer.Trim(),

                    position = r.Position,
                    panel = r.Panel,
                    width = r.Width,
                    height = r.Height
                };

                _log.LogInformation(
                    "Calling image composition service at {Url}. " +
                    "Dimensions: {Width}x{Height}, Position: {Position}",
                    url,
                    r.Width,
                    r.Height,
                    r.Position);

                using var resp = await http.PostAsJsonAsync(
                    url,
                    payload,
                    cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(
                        CancellationToken.None);

                    _log.LogError(
                        "Image service returned HTTP {StatusCode}. " +
                        "URL: {Url}. Response: {Body}",
                        (int)resp.StatusCode,
                        url,
                        body);

                    throw new HttpRequestException(
                        $"Image service returned HTTP {(int)resp.StatusCode}: {body}");
                }

                var contentType = resp.Content.Headers.ContentType?.MediaType;

                if (!string.Equals(
                        contentType,
                        "image/png",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var body = await resp.Content.ReadAsStringAsync(
                        CancellationToken.None);

                    _log.LogError(
                        "Image service returned unexpected content type {ContentType}. " +
                        "Response: {Body}",
                        contentType,
                        body);

                    throw new InvalidOperationException(
                        $"Expected image/png but received {contentType ?? "unknown content type"}.");
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);

                if (bytes.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Image service returned an empty PNG.");
                }

                _log.LogInformation(
                    "Image composition completed successfully. Output size: {Bytes} bytes.",
                    bytes.Length);

                return bytes;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogError(
                    "Image composition exceeded the configured timeout of {TimeoutSeconds} seconds.",
                    o.TimeoutSeconds);

                throw new TimeoutException(
                    $"Image generation exceeded the timeout of {o.TimeoutSeconds} seconds.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<byte[]> NormalizeAsync(byte[] image, CancellationToken ct) =>
    PostImageAsync("/normalize", image, new(), gpu: false, ct);

        public Task<byte[]> EditAsync(byte[] image, string prompt, CancellationToken ct) =>
            PostImageAsync("/edit", image, new() { ["prompt"] = prompt }, gpu: true, ct);

        public Task<byte[]> OverlayAsync(byte[] image, string title, string? subtitle, string? footer, string position, CancellationToken ct)
        {
            var f = new Dictionary<string, string> { ["title"] = title, ["position"] = position };
            if (!string.IsNullOrWhiteSpace(subtitle)) f["subtitle"] = subtitle;
            if (!string.IsNullOrWhiteSpace(footer)) f["footer"] = footer;
            return PostImageAsync("/overlay", image, f, gpu: false, ct);
        }

        private async Task<byte[]> PostImageAsync(string path, byte[] image, Dictionary<string, string> fields, bool gpu, CancellationToken ct)
        {
            var o = _opts.CurrentValue;
            if (!o.Enabled) throw new InvalidOperationException("Image generation is disabled.");

            if (gpu && !await _gate.WaitAsync(TimeSpan.FromSeconds(60), ct))
                throw new TimeoutException("Image generator is busy. Please try again shortly.");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));

                using var form = new MultipartFormDataContent();
                var part = new ByteArrayContent(image);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(part, "image", "image.png");
                foreach (var kv in fields) form.Add(new StringContent(kv.Value), kv.Key);

                var http = _factory.CreateClient("ImageGen");
                using var resp = await http.PostAsync($"{o.BaseUrl.TrimEnd('/')}{path}", form, cts.Token);

                if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    throw new ArgumentException("That file could not be read as an image.");
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogError("Image service {Path} returned {Status}", path, (int)resp.StatusCode);
                    throw new HttpRequestException($"Image service error {(int)resp.StatusCode}");
                }
                return await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            finally { if (gpu) _gate.Release(); }
        }
    }
}