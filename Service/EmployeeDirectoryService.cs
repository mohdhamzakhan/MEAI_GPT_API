// Service/Models/EmployeeDirectoryService.cs
//
// Minimal starter implementation: a hand-maintained JSON file mapping
// userId -> employee eligibility attributes. Deliberately behind an
// interface so swapping this for a real HR-system API call later doesn't
// touch any calling code — every caller only ever sees
// IEmployeeDirectoryService.

using System.Text.Json;

namespace MEAI_GPT_API.Service.Models
{
    public class EmployeeRecord
    {
        public string UserId { get; set; } = "";
        public string? Grade { get; set; }              // e.g. "Deputy Manager" — a job title, resolved to a band separately
        public string? EmployeeCategory { get; set; }    // "Direct" | "Indirect"
        public string? DirectSubtype { get; set; }       // "Direct Worker" | "Administrative Staff" — only set when EmployeeCategory = Direct
    }

    public interface IEmployeeDirectoryService
    {
        /// <summary>
        /// Returns what's known for this user. Missing/unknown fields come
        /// back null — callers must not silently default an unknown field,
        /// per the eligibility design's "don't guess" principle. Returns
        /// an all-null record (not an exception) for a userId with no match.
        /// </summary>
        Task<EmployeeRecord> GetEmployeeInfoAsync(string userId);
    }

    public class JsonFileEmployeeDirectoryService : IEmployeeDirectoryService
    {
        private readonly ILogger<JsonFileEmployeeDirectoryService> _logger;
        private readonly string _filePath;
        private Dictionary<string, EmployeeRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        public JsonFileEmployeeDirectoryService(IConfiguration configuration, ILogger<JsonFileEmployeeDirectoryService> logger)
        {
            _logger = logger;
            _filePath = configuration["EmployeeDirectory:FilePath"] ?? "./context/employee-directory.json";
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _logger.LogWarning($"⚠️ Employee directory file not found at {_filePath} — grade/category-based filtering will be inactive for all users until this exists");
                    return;
                }

                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<EmployeeRecord>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

                _records = list.ToDictionary(r => r.UserId, r => r, StringComparer.OrdinalIgnoreCase);
                _logger.LogInformation($"📋 Loaded {_records.Count} employee directory records from {_filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to load employee directory from {_filePath} — treating all users as unknown");
                _records = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        public Task<EmployeeRecord> GetEmployeeInfoAsync(string userId)
        {
            if (_records.TryGetValue(userId, out var record))
                return Task.FromResult(record);

            _logger.LogWarning($"⚠️ No employee directory entry for userId '{userId}' — eligibility filters will be skipped for this request, not guessed");
            return Task.FromResult(new EmployeeRecord { UserId = userId });
        }
    }
}