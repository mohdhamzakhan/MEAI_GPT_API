using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace MEAI_GPT_API.Models
{
    public class AttachmentModels
    {
    }

    public class ChatAttachment
    {
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public bool IsImage { get; set; }
        public string? Base64Image { get; set; }   // images only
        public string? ExtractedText { get; set; } // documents only
    }

    public class StreamWithFilesRequest
    {
        public string Question { get; set; } = "";
        public string Plant { get; set; } = "";
        public string? SessionId { get; set; }
        public bool MeaiInfo { get; set; } = true;
        public int MaxResults { get; set; } = 10;
        public IFormFileCollection? Files { get; set; }
    }

    public class AttachmentProcessor
    {
        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp" };
        private static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".docx", ".xlsx", ".txt", ".csv" };

        private const long MaxFileBytes = 15 * 1024 * 1024;
        private const int MaxImages = 4;
        private const int MaxDocChars = 60_000; // ~15k tokens per document
        private const int ImageUpscaleFactor = 4;
        private const int MaxProcessedImageWidth = 8192;
        private const int MaxProcessedImageHeight = 4096;
        private readonly IDocumentProcessor _docProcessor;
        private readonly ILogger<AttachmentProcessor> _logger;

        public AttachmentProcessor(IDocumentProcessor docProcessor, ILogger<AttachmentProcessor> logger)
        {
            _docProcessor = docProcessor;
            _logger = logger;
        }

        public async Task<List<ChatAttachment>> ProcessAsync(IEnumerable<IFormFile> files)
        {
            var result = new List<ChatAttachment>();

            foreach (var file in files)
            {
                if (file.Length == 0 || file.Length > MaxFileBytes)
                    throw new ArgumentException($"{file.FileName}: empty or larger than 15 MB");

                var ext = Path.GetExtension(file.FileName);

                if (ImageExt.Contains(ext))
                {
                    if (result.Count(a => a.IsImage) >= MaxImages)
                        throw new ArgumentException($"Maximum {MaxImages} images per message");

                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);

                    result.Add(new ChatAttachment
                    {
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        IsImage = true,
                        Base64Image = Convert.ToBase64String(ms.ToArray())
                    });
                }
                else if (DocExt.Contains(ext))
                {
                    // Save to a temp file because ExtractTextAsync takes a path
                    var tmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{ext}");
                    try
                    {
                        await using (var fs = File.Create(tmp))
                            await file.CopyToAsync(fs);

                        var text = await _docProcessor.ExtractTextAsync(tmp);
                        if (text.Length > MaxDocChars)
                            text = text[..MaxDocChars] + "\n[... document truncated ...]";

                        result.Add(new ChatAttachment
                        {
                            FileName = file.FileName,
                            ContentType = file.ContentType,
                            IsImage = false,
                            ExtractedText = text
                        });
                    }
                    finally { if (File.Exists(tmp)) File.Delete(tmp); }
                }
                else
                {
                    throw new ArgumentException($"{file.FileName}: unsupported file type '{ext}'");
                }
            }
            return result;
        }

        private async Task<byte[]> PrepareImageForVisionAsync(IFormFile file)
        {
            await using var input = file.OpenReadStream();

            using var image = await Image.LoadAsync(input);

            _logger.LogInformation(
                "Original image: {FileName}, Width={Width}, Height={Height}",
                file.FileName,
                image.Width,
                image.Height);

            var targetWidth = image.Width * ImageUpscaleFactor;
            var targetHeight = image.Height * ImageUpscaleFactor;

            // Prevent excessively large images.
            if (targetWidth > MaxProcessedImageWidth)
            {
                var scale = (double)MaxProcessedImageWidth / targetWidth;
                targetWidth = MaxProcessedImageWidth;
                targetHeight = Math.Max(1, (int)(targetHeight * scale));
            }

            if (targetHeight > MaxProcessedImageHeight)
            {
                var scale = (double)MaxProcessedImageHeight / targetHeight;
                targetHeight = MaxProcessedImageHeight;
                targetWidth = Math.Max(1, (int)(targetWidth * scale));
            }

            image.Mutate(x =>
            {
                x.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                });
            });

            await using var output = new MemoryStream();

            await image.SaveAsPngAsync(output);

            var processed = output.ToArray();

            _logger.LogInformation(
                "Processed image: {FileName}, Width={Width}, Height={Height}, Bytes={Bytes}",
                file.FileName,
                targetWidth,
                targetHeight,
                processed.Length);

            return processed;
        }
    }
}
