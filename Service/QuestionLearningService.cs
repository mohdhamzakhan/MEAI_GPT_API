using MEAI_GPT_API.Service;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Services
{
    public class AutoLearningOptions
    {
        public bool Enabled { get; set; }
        public bool AutoPromote { get; set; }
        public int MinDistinctUsers { get; set; } = 3;
        public double MinConfidence { get; set; } = 0.8;
        public int MaxSourcesPerAnswer { get; set; } = 2;
    }

    public class LearningCandidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Question { get; set; } = "";      // first phrasing seen (used as the trigger text)
        public string Norm { get; set; } = "";          // normalized, for matching
        public string SourceFile { get; set; } = "";
        public List<string> UserHashes { get; set; } = new();   // hashed, never raw user ids
        public int Count { get; set; }
        public double AvgConfidence { get; set; }
        public string Status { get; set; } = "candidate";       // candidate | promoted | rejected
        public string Origin { get; set; } = "auto";            // auto | appreciation | admin
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    public class QuestionLearningService
    {
        private static readonly object _lock = new();
        private static List<LearningCandidate>? _items;

        private readonly string _file = Path.Combine(AppContext.BaseDirectory, "context", "learning-candidates.json");
        private readonly AutoLearningOptions _opt;
        private readonly LearnedTriggerService _learned;
        private readonly ILogger<QuestionLearningService> _log;

        public QuestionLearningService(IOptions<AutoLearningOptions> opt,
            LearnedTriggerService learned, ILogger<QuestionLearningService> log)
        { _opt = opt.Value; _learned = learned; _log = log; }

        // A verified, grounded, complete answer. Counts it; promotes only at threshold.
        public async Task RecordVerifiedAsync(string question, string userId,
            IEnumerable<string> sources, double confidence)
        {
            if (!_opt.Enabled || confidence < _opt.MinConfidence) return;

            var srcs = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (srcs.Count == 0 || srcs.Count > _opt.MaxSourcesPerAnswer) return;

            var norm = Normalize(question);
            if (norm.Split(' ').Length < 3) return;
            var uh = Hash(userId);
            var toPromote = new List<LearningCandidate>();

            lock (_lock)
            {
                Load();
                foreach (var src in srcs)
                {
                    var c = _items!.FirstOrDefault(x =>
                        x.SourceFile.Equals(src, StringComparison.OrdinalIgnoreCase) && Similar(x.Norm, norm));
                    if (c == null)
                    {
                        c = new LearningCandidate { Question = question, Norm = norm, SourceFile = src };
                        _items.Add(c);
                    }
                    if (c.Status != "candidate") continue;

                    c.AvgConfidence = ((c.AvgConfidence * c.Count) + confidence) / (c.Count + 1);
                    c.Count++;
                    c.LastSeen = DateTime.Now;
                    if (!c.UserHashes.Contains(uh)) c.UserHashes.Add(uh);

                    if (_opt.AutoPromote && c.UserHashes.Count >= _opt.MinDistinctUsers)
                    {
                        c.Status = "promoted";
                        toPromote.Add(c);
                    }
                }
                Save();
            }

            foreach (var c in toPromote)
            {
                await _learned.PromoteAsync(c.Question, c.SourceFile, LearnedTriggerService.OriginAuto);
                _log.LogInformation("🧠 Auto-learned: '{Q}' -> {Src} ({N} users)", c.Question, c.SourceFile, c.UserHashes.Count);
            }
        }

        // A human clicked 👍: strong enough to promote immediately.
        public async Task PromoteAppreciatedAsync(string question, string source)
        {
            if (!_opt.Enabled) return;
            lock (_lock)
            {
                Load();
                var norm = Normalize(question);
                var c = _items!.FirstOrDefault(x => x.SourceFile.Equals(source, StringComparison.OrdinalIgnoreCase) && Similar(x.Norm, norm));
                if (c?.Status == "rejected") return;      // an admin said no: respect it
                if (c == null) { c = new LearningCandidate { Question = question, Norm = norm, SourceFile = source }; _items.Add(c); }
                c.Status = "promoted"; c.Origin = "appreciation"; c.LastSeen = DateTime.Now;
                Save();
            }
            await _learned.PromoteAsync(question, source, LearnedTriggerService.OriginAppreciation);
        }

        // ── admin review ──
        public List<LearningCandidate> GetPending() { lock (_lock) { Load(); return _items!.Where(x => x.Status == "candidate").OrderByDescending(x => x.UserHashes.Count).ToList(); } }

        public async Task<bool> ApproveAsync(string id)
        {
            LearningCandidate? c;
            lock (_lock) { Load(); c = _items!.FirstOrDefault(x => x.Id == id); if (c == null) return false; c.Status = "promoted"; c.Origin = "admin"; Save(); }
            await _learned.PromoteAsync(c.Question, c.SourceFile, LearnedTriggerService.OriginCorrection);
            return true;
        }

        public bool Reject(string id)
        {
            lock (_lock) { Load(); var c = _items!.FirstOrDefault(x => x.Id == id); if (c == null) return false; c.Status = "rejected"; Save(); return true; }
        }

        // ── helpers ──
        private void Load()
        {
            if (_items != null) return;
            try { _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<LearningCandidate>>(File.ReadAllText(_file)) ?? new() : new(); }
            catch { _items = new(); }
        }
        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
        }
        private static string Normalize(string s) =>
            Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^\w\s]", " "), @"\s+", " ").Trim();
        private static bool Similar(string a, string b)
        {
            var A = a.Split(' ').ToHashSet(); var B = b.Split(' ').ToHashSet();
            return A.Count > 0 && B.Count > 0 && (double)A.Intersect(B).Count() / A.Union(B).Count() >= 0.8;
        }
        private static string Hash(string s) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s ?? "")))[..12];
    }
}