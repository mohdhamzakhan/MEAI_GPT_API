using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MEAI_GPT_API.Service
{
    public class ImageWorkspaceOptions
    {
        public string Folder { get; set; } = @"D:\MEAI\images";
        public string VisionModel { get; set; } = "";
        public int MaxUploadMB { get; set; } = 12;
        public int MaxImagesPerSession { get; set; } = 30;
    }

    public class ImageRecord
    {
        public string Id { get; set; } = "";
        public int Number { get; set; }                 // the "Image 3" label the user sees
        public string SessionId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Kind { get; set; } = "";          // uploaded | edited | text-added | generated
        public string? ParentId { get; set; }           // which image this was made from
        public string Label { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class ImageLibraryService
    {
        private static readonly Regex SafeSession = new(@"^[A-Za-z0-9_-]{4,64}$", RegexOptions.Compiled);
        private static readonly Regex SafeImageId = new(@"^[a-f0-9]{32}$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
        private readonly IOptionsMonitor<ImageWorkspaceOptions> _opts;

        public ImageLibraryService(IOptionsMonitor<ImageWorkspaceOptions> opts) { _opts = opts; }

        private string Dir(string sessionId)
        {
            if (!SafeSession.IsMatch(sessionId)) throw new ArgumentException("Invalid session id.");
            var d = Path.Combine(_opts.CurrentValue.Folder, sessionId);
            Directory.CreateDirectory(d);
            return d;
        }

        private static List<ImageRecord> Load(string dir)
        {
            var f = Path.Combine(dir, "index.json");
            if (!File.Exists(f)) return new();
            try { return JsonSerializer.Deserialize<List<ImageRecord>>(File.ReadAllText(f)) ?? new(); }
            catch { return new(); }
        }

        public async Task<ImageRecord> SaveAsync(string sessionId, string userId, string kind,
            string? parentId, string label, byte[] png)
        {
            var gate = Gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var dir = Dir(sessionId);
                var list = Load(dir);
                if (list.Count >= _opts.CurrentValue.MaxImagesPerSession)
                    throw new InvalidOperationException("Image limit reached for this conversation. Start a new one.");

                var rec = new ImageRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Number = list.Count + 1,
                    SessionId = sessionId,
                    UserId = userId,
                    Kind = kind,
                    ParentId = parentId,
                    Label = label.Length > 60 ? label[..60] : label
                };

                await File.WriteAllBytesAsync(Path.Combine(dir, rec.Id + ".png"), png);
                list.Add(rec);
                var tmp = Path.Combine(dir, "index.json.tmp");
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(list));
                File.Move(tmp, Path.Combine(dir, "index.json"), overwrite: true);
                return rec;
            }
            finally { gate.Release(); }
        }

        public List<ImageRecord> List(string sessionId, string userId) =>
            Load(Dir(sessionId)).Where(r => r.UserId == userId).ToList();

        public async Task<(ImageRecord Rec, byte[] Bytes)?> GetAsync(string sessionId, string userId, string imageId)
        {
            if (!SafeImageId.IsMatch(imageId)) return null;
            var rec = List(sessionId, userId).FirstOrDefault(r => r.Id == imageId);
            if (rec == null) return null;
            var path = Path.Combine(Dir(sessionId), imageId + ".png");
            return File.Exists(path) ? (rec, await File.ReadAllBytesAsync(path)) : null;
        }

        public void DeleteSession(string sessionId)
        {
            if (!SafeSession.IsMatch(sessionId)) return;
            var d = Path.Combine(_opts.CurrentValue.Folder, sessionId);
            if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }
}