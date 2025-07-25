public interface IMetricsCollector
{
    void RecordCacheOperation(string operation, long durationMs, bool success);
    void RecordCacheError(string operation);
    void RecordEmbeddingOperation(long durationMs, int tokenCount, bool success);
    void RecordChromaDBOperation(string operation, long durationMs, bool success);
    void RecordQueryProcessing(long durationMs, int chunkCount, bool success);
    void RecordModelInference(string model, long durationMs, int tokenCount, bool success);
}