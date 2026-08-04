// ============================================================
// FILE 2: AppreciatedAnswerStore.cs  (NEW FILE)
// Replaces the unbounded ConcurrentBag<> with a bounded,
// thread-safe store. Registered as Singleton.
// ============================================================
using System.Collections.Concurrent;
using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Services
{
    public class AppreciatedAnswerStore
    {
        private const int MaxEntries = 500;

        // Use a Queue so we can evict the oldest entry when full
        private readonly ConcurrentQueue<(string Question, string Answer, List<RelevantChunk> Chunks)> _store = new();
        private int _count; // Interlocked counter, avoids .Count() scan

        public void Add(string question, string answer, List<RelevantChunk> chunks)
        {
            _store.Enqueue((question, answer, chunks));
            Interlocked.Increment(ref _count);

            // Evict oldest when over capacity
            while (_count > MaxEntries && _store.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }

        public IEnumerable<(string Question, string Answer, List<RelevantChunk> Chunks)> All()
            => _store;

        public int Count => _count;
    }
}
