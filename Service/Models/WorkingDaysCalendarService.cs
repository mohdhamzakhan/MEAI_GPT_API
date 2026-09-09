// Service/Models/WorkingDaysCalendarService.cs
//
// Loads the company's actual working-day calendar (weekends and holidays
// already excluded, one figure per calendar month) from
// context/working-days-calendar.json and uses it to compute real elapsed
// working days between two dates — e.g. an employee's joining date and
// today — for leave-accrual calculations such as Earned Leave (1 day per
// 10.73 working days).
//
// Deliberately keyed by (year, month) and loaded from a maintained JSON
// file rather than derived from a "weekdays minus weekends" formula: the
// real figure also has to subtract public/company holidays, which vary by
// year and can't be computed from a formula alone. Whoever maintains HR
// data updates this file; this service just looks it up.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MEAI_GPT_API.Service.Models
{
    public class MonthlyWorkingDaysEntry
    {
        [JsonPropertyName("year")]
        public int Year { get; set; }

        [JsonPropertyName("month")]
        public int Month { get; set; }

        [JsonPropertyName("month_name")]
        public string MonthName { get; set; } = "";

        [JsonPropertyName("working_days")]
        public int WorkingDays { get; set; }
    }

    public class WorkingDaysCalendarService
    {
        private readonly ILogger<WorkingDaysCalendarService> _logger;
        private readonly Dictionary<(int Year, int Month), int> _table;

        public WorkingDaysCalendarService(IConfiguration configuration, ILogger<WorkingDaysCalendarService> logger)
        {
            _logger = logger;
            _table = Load(configuration);
        }

        private Dictionary<(int, int), int> Load(IConfiguration configuration)
        {
            var path = configuration["WorkingDaysCalendar:FilePath"] ?? "./context/working-days-calendar.json";
            try
            {
                if (!File.Exists(path))
                {
                    _logger.LogWarning($"⚠️ Working-days calendar not found at {path} — EL-style calculations will fall back to a calendar-day approximation until this file is added.");
                    return new Dictionary<(int, int), int>();
                }

                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<MonthlyWorkingDaysEntry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<MonthlyWorkingDaysEntry>();

                var table = entries.ToDictionary(e => (e.Year, e.Month), e => e.WorkingDays);

                if (table.Count > 0)
                {
                    var minYear = entries.Min(e => e.Year);
                    var maxYear = entries.Max(e => e.Year);
                    _logger.LogInformation($"📅 Working-days calendar loaded: {table.Count} months covering {minYear}–{maxYear}");
                }
                else
                {
                    _logger.LogWarning("⚠️ Working-days calendar file was found but contained no entries.");
                }

                return table;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load working-days calendar — EL-style calculations will fall back to a calendar-day approximation.");
                return new Dictionary<(int, int), int>();
            }
        }

        public bool HasAnyData => _table.Count > 0;

        public bool HasDataFor(int year, int month) => _table.ContainsKey((year, month));

        /// <summary>
        /// Computes the number of working days between start and end
        /// (both inclusive) using the monthly calendar. Only whole-month
        /// totals are available, so a month that is only partially covered
        /// by the range (the joining month, and the current/"today" month)
        /// is prorated by its share of that month's calendar days — this is
        /// an approximation for those two edge months only; every fully
        /// covered month in between uses its exact figure from the table.
        ///
        /// Returns null if the range touches any month missing from the
        /// calendar, so the caller can fall back cleanly (e.g. to a raw
        /// calendar-day count) instead of silently under/over-counting.
        /// </summary>
        public double? GetWorkingDaysBetween(DateTime start, DateTime end)
        {
            if (end.Date < start.Date) return 0;

            var cursor = new DateTime(start.Year, start.Month, 1);
            var endMonth = new DateTime(end.Year, end.Month, 1);
            double total = 0;

            while (cursor <= endMonth)
            {
                if (!_table.TryGetValue((cursor.Year, cursor.Month), out var monthWorkingDays))
                {
                    return null; // missing data for a month the range touches
                }

                int daysInMonth = DateTime.DaysInMonth(cursor.Year, cursor.Month);
                int rangeStartDay = (cursor.Year == start.Year && cursor.Month == start.Month) ? start.Day : 1;
                int rangeEndDay = (cursor.Year == end.Year && cursor.Month == end.Month) ? end.Day : daysInMonth;
                int daysCoveredInMonth = rangeEndDay - rangeStartDay + 1;

                total += (daysCoveredInMonth == daysInMonth)
                    ? monthWorkingDays // full month — use the exact figure
                    : monthWorkingDays * ((double)daysCoveredInMonth / daysInMonth); // partial month — prorated

                cursor = cursor.AddMonths(1);
            }

            return total;
        }
    }
}