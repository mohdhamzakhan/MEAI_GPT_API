// Observability/RagTelemetry.cs
//
// Step 10 "Observability".
//
//   RagTelemetry   ActivitySource (traces) + Meter (metrics), both named "MEAI.Rag".
//   RagTrace       one per user question. Records stage timings and chunk counts per stage,
//                  and emits an OpenTelemetry span for the request and for every stage.
//   RagTraceStore  singleton ring buffer of the last 300 requests, read by the dashboard.
//
// Every stage entry carries: duration, chunk count, how many chunks are Centralized, and the
// top sources. That is what shows "Centralized chunks existed at retrieval but vanished at
// the rerank stage" without reading logs.
//
// Privacy: question text (first 200 chars) and user id are kept IN MEMORY only, and are
// exposed only through [Authorize]d endpoints.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Observability
{
    public static class RagTelemetry
    {
        public const string Name = "MEAI.Rag";

        public static readonly ActivitySource Source = new(Name);
        public static readonly Meter Meter = new(Name);

        public static readonly Counter<long> Requests =
            Meter.CreateCounter<long>("rag.requests", description: "Questions received");

        public static readonly Counter<long> Outcomes =
            Meter.CreateCounter<long>("rag.outcomes", description: "Questions by outcome (answered, declined, clarified, smalltalk, cache, error, aborted)");

        public static readonly Histogram<double> RequestMs =
            Meter.CreateHistogram<double>("rag.request.duration", unit: "ms", description: "End-to-end time per question");

        public static readonly Histogram<double> StageMs =
            Meter.CreateHistogram<double>("rag.stage.duration", unit: "ms", description: "Time per pipeline stage");

        public static readonly Histogram<long> StageChunks =
            Meter.CreateHistogram<long>("rag.stage.chunks", description: "Chunks remaining after each stage");

        public static readonly Counter<long> GroundingFailures =
            Meter.CreateCounter<long>("rag.grounding.failures", description: "Answers rejected by the verifier");
    }

    public sealed class StageRecord
    {
        public string Name { get; set; } = "";
        public double Ms { get; set; }
        public int? Chunks { get; set; }
        public int? Centralized { get; set; }
        public List<string> Sources { get; set; } = new();
        public string? Note { get; set; }
    }

    public sealed class RagRequestTrace
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string? TraceId { get; set; }
        public DateTime At { get; set; } = DateTime.Now;
        public string Question { get; set; } = "";
        public string Plant { get; set; } = "";
        public string? UserId { get; set; }
        public string? SessionId { get; set; }
        public string Outcome { get; set; } = "running";
        public double TotalMs { get; set; }
        public double? Confidence { get; set; }
        public string? Note { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new();
        public List<StageRecord> Stages { get; set; } = new();
    }

    public sealed class RagTrace : IDisposable
    {
        private static readonly AsyncLocal<RagTrace?> CurrentTrace = new();
        private static readonly IDisposable Noop = new NoopScope();

        /// <summary>The trace of the question being processed on this async flow (null outside a request).</summary>
        public static RagTrace? Current => CurrentTrace.Value;

        /// <summary>Safe to call anywhere: returns a no-op scope when there is no active trace.</summary>
        public static IDisposable Step(string name) => Current?.Stage(name) ?? Noop;

        private readonly RagTraceStore _store;
        private readonly Activity? _root;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _completed;

        public RagRequestTrace Data { get; }

        internal RagTrace(RagTraceStore store, RagRequestTrace data)
        {
            _store = store;
            Data = data;

            _root = RagTelemetry.Source.StartActivity("rag.request");
            _root?.SetTag("rag.plant", data.Plant);
            _root?.SetTag("rag.session", data.SessionId);
            Data.TraceId = _root?.TraceId.ToString();

            RagTelemetry.Requests.Add(1, new KeyValuePair<string, object?>("plant", data.Plant));
            CurrentTrace.Value = this;
        }

        public IDisposable Stage(string name) => new StageScope(this, name);

        /// <summary>Records how many chunks survived a stage (and how many are Centralized).</summary>
        public void Chunks(string stage, IEnumerable<RelevantChunk>? chunks)
        {
            var list = chunks?.ToList() ?? new List<RelevantChunk>();
            var centralized = list.Count(c => string.Equals(c.PlantTag, "centralized", StringComparison.OrdinalIgnoreCase));

            lock (Data.Stages)
            {
                Data.Stages.Add(new StageRecord
                {
                    Name = stage,
                    Chunks = list.Count,
                    Centralized = centralized,
                    Sources = list.Select(c => c.Source ?? "?").Distinct().Take(6).ToList()
                });
            }

            RagTelemetry.StageChunks.Record(list.Count, new KeyValuePair<string, object?>("stage", stage));
            _root?.AddEvent(new ActivityEvent($"chunks:{stage}",
                tags: new ActivityTagsCollection { { "count", list.Count }, { "centralized", centralized } }));
        }

        public void Tag(string key, object? value)
        {
            Data.Tags[key] = value?.ToString() ?? "";
            _root?.SetTag($"rag.{key}", value);
        }

        public void Note(string note) => Data.Note = note;

        /// <summary>outcome: answered | declined | clarified | smalltalk | cache | error</summary>
        public void Complete(string outcome, double? confidence = null, string? note = null)
        {
            if (_completed) return;
            _completed = true;

            Data.Outcome = outcome;
            Data.TotalMs = _clock.Elapsed.TotalMilliseconds;
            Data.Confidence = confidence;
            if (note != null) Data.Note = note;

            var tag = new KeyValuePair<string, object?>("outcome", outcome);
            RagTelemetry.Outcomes.Add(1, tag);
            RagTelemetry.RequestMs.Record(Data.TotalMs, tag);
            if (outcome == "declined") RagTelemetry.GroundingFailures.Add(1);

            _root?.SetTag("rag.outcome", outcome);
            if (confidence.HasValue) _root?.SetTag("rag.confidence", confidence.Value);
            if (outcome == "error") _root?.SetStatus(ActivityStatusCode.Error, note);

            _store.Add(Data);
        }

        public void Dispose()
        {
            if (!_completed) Complete("aborted");   // client disconnected / exception before an outcome
            _root?.Dispose();
        }

        private sealed class StageScope : IDisposable
        {
            private readonly RagTrace _owner;
            private readonly string _name;
            private readonly Activity? _span;
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public StageScope(RagTrace owner, string name)
            {
                _owner = owner;
                _name = name;
                _span = RagTelemetry.Source.StartActivity(
                    name, ActivityKind.Internal, owner._root?.Context ?? default);
            }

            public void Dispose()
            {
                _sw.Stop();
                var ms = _sw.Elapsed.TotalMilliseconds;

                lock (_owner.Data.Stages)
                    _owner.Data.Stages.Add(new StageRecord { Name = _name, Ms = Math.Round(ms, 1) });

                RagTelemetry.StageMs.Record(ms, new KeyValuePair<string, object?>("stage", _name));
                _span?.Dispose();
            }
        }

        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }

    public sealed class RagTraceStore
    {
        private const int Capacity = 300;
        private readonly ConcurrentQueue<RagRequestTrace> _items = new();

        public RagTrace Begin(string question, string plant, string? userId, string? sessionId)
        {
            var data = new RagRequestTrace
            {
                Question = question.Length > 200 ? question[..200] : question,
                Plant = plant,
                UserId = userId,
                SessionId = sessionId
            };
            return new RagTrace(this, data);
        }

        internal void Add(RagRequestTrace trace)
        {
            _items.Enqueue(trace);
            while (_items.Count > Capacity && _items.TryDequeue(out _)) { }
        }

        public List<RagRequestTrace> Recent(int take = 50, string? outcome = null)
        {
            var query = _items.Reverse();
            if (!string.IsNullOrWhiteSpace(outcome))
                query = query.Where(t => t.Outcome.Equals(outcome, StringComparison.OrdinalIgnoreCase));
            return query.Take(Math.Clamp(take, 1, Capacity)).ToList();
        }

        public object Summary()
        {
            var all = _items.ToList();
            var lastHour = all.Where(t => t.At > DateTime.Now.AddHours(-1)).ToList();

            static double Pct(List<double> v, double p)
            {
                if (v.Count == 0) return 0;
                v.Sort();
                return Math.Round(v[(int)Math.Min(v.Count - 1, Math.Ceiling(p * v.Count) - 1)], 0);
            }

            var finishedMs = all.Where(t => t.Outcome is not ("running" or "aborted")).Select(t => t.TotalMs).ToList();

            var stages = all
                .SelectMany(t => t.Stages.Where(s => s.Ms > 0))
                .GroupBy(s => s.Name)
                .Select(g =>
                {
                    var ms = g.Select(s => s.Ms).ToList();
                    return new { stage = g.Key, count = ms.Count, avgMs = Math.Round(ms.Average(), 0), p95Ms = Pct(ms, 0.95) };
                })
                .OrderByDescending(s => s.avgMs)
                .ToList();

            // retrieval health: of answered questions, how many had Centralized chunks reach generation
            var finals = all.Where(t => t.Outcome == "answered")
                .Select(t => t.Stages.LastOrDefault(s => s.Name.StartsWith("final")))
                .Where(s => s != null).ToList();

            return new
            {
                total = all.Count,
                lastHour = lastHour.Count,
                outcomes = all.GroupBy(t => t.Outcome).ToDictionary(g => g.Key, g => g.Count()),
                latencyMs = new { p50 = Pct(finishedMs.ToList(), 0.5), p95 = Pct(finishedMs.ToList(), 0.95) },
                stages,
                answeredWithCentralizedChunks = finals.Count == 0 ? (double?)null :
                    Math.Round(100.0 * finals.Count(s => (s!.Centralized ?? 0) > 0) / finals.Count, 1),
                avgFinalChunks = finals.Count == 0 ? (double?)null : Math.Round(finals.Average(s => s!.Chunks ?? 0), 1)
            };
        }
    }
}