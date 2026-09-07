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

        /// <summary>
        /// Upserts a record — used both when the chat flow asks an unknown
        /// employee for their designation, and when someone updates their
        /// own record directly (e.g. after a promotion). Updates the
        /// in-memory cache immediately (this service is registered as a
        /// Singleton, so the change is visible to every request app-wide
        /// right away, not just the caller's) and rewrites the JSON file so
        /// it survives an app restart. Returns false if the write failed —
        /// callers should treat that as "not persisted" and warn the user
        /// their answer won't be remembered next session, without blocking
        /// on it in the current request.
        /// </summary>
        Task<bool> SetEmployeeInfoAsync(EmployeeRecord record);
    }

    public class JsonFileEmployeeDirectoryService : IEmployeeDirectoryService
    {
        private readonly ILogger<JsonFileEmployeeDirectoryService> _logger;
        private readonly string _filePath;
        private Dictionary<string, EmployeeRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        // Guards read-modify-write of _records and the JSON file. This
        // service is a Singleton (see Program.cs), so concurrent requests
        // from different users can race on an upsert without this.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

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

        public async Task<bool> SetEmployeeInfoAsync(EmployeeRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.UserId))
            {
                _logger.LogWarning("⚠️ Refusing to save an employee record with no UserId");
                return false;
            }

            await _writeLock.WaitAsync();
            try
            {
                // Upsert into the in-memory cache first — this is what every
                // in-flight and future request actually reads from, so the
                // employee's very next message (even in the request that's
                // resolving their answer) sees the update, without waiting
                // on the file write below.
                _records[record.UserId] = record;

                try
                {
                    var json = JsonSerializer.Serialize(_records.Values.ToList(),
                        new JsonSerializerOptions { WriteIndented = true });

                    // Write to a temp file then move it into place, so a
                    // crash or concurrent read mid-write can't leave the
                    // directory file half-written/corrupted.
                    var tempPath = _filePath + ".tmp";
                    await File.WriteAllTextAsync(tempPath, json);
                    File.Move(tempPath, _filePath, overwrite: true);

                    _logger.LogInformation($"📋 Saved designation for userId '{record.UserId}' (Grade='{record.Grade}', Category='{record.EmployeeCategory}') to {_filePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    // The in-memory cache is already updated above, so this
                    // request and the rest of this app run still benefit
                    // from the answer — only the "survives a restart" part
                    // failed. Surface that distinction to the caller.
                    _logger.LogError(ex, $"❌ Failed to persist employee directory to {_filePath} — update is in-memory only until a restart");
                    return false;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}