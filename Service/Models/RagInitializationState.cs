namespace MEAI_GPT_API.Service.Models
{
    /// <summary>
    /// Tracks whether RAG indexing has completed, as a SINGLETON -- separate
    /// from DynamicRagService's own _systemInitialized field, which is
    /// useless for this purpose because IRAGService is registered AddScoped
    /// (one new DynamicRagService instance per HTTP request). Setting
    /// _systemInitialized = true on the instance used by the background
    /// startup init, or by a manual /api/Rag/refresh-embeddings call, has zero
    /// effect on the instance created for the next query request -- each one
    /// starts with _systemInitialized defaulting back to false. That bug
    /// caused every single query to be permanently rejected with "still
    /// starting up", forever, regardless of how many times indexing actually
    /// completed, since a fresh per-request instance can never observe another
    /// instance's field.
    ///
    /// This class is deliberately NOT a replacement for making
    /// DynamicRagService itself a singleton -- that would introduce real
    /// concurrency bugs elsewhere, since the class has several other instance
    /// fields (e.g. _pendingAmbiguousGradeMention) that correctly assume
    /// per-request isolation. Only the readiness flag needs to be shared
    /// across instances; nothing else does.
    /// </summary>
    public class RagInitializationState
    {
        private volatile bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;

        public void MarkInitialized() => _isInitialized = true;
    }
}