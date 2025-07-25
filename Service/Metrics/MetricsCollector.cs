using System.Diagnostics;
using System.Diagnostics.Metrics;

public class MetricsCollector : IMetricsCollector
{
    private readonly Meter _meter;
    private readonly Counter<long> _cacheErrors;
    private readonly Histogram<double> _cacheDuration;
    private readonly Histogram<double> _embeddingDuration;
    private readonly Histogram<double> _chromaDBDuration;
    private readonly Histogram<double> _queryProcessingDuration;
    private readonly Histogram<double> _modelInferenceDuration;
    private readonly ILogger<MetricsCollector> _logger;

    public MetricsCollector(ILogger<MetricsCollector> logger)
    {
        _logger = logger;
        _meter = new Meter("MEAI.RAG.Metrics", "1.0.0");

        _cacheErrors = _meter.CreateCounter<long>(
            "rag_cache_errors",
            description: "Number of cache operation errors");

        _cacheDuration = _meter.CreateHistogram<double>(
            "rag_cache_duration",
            unit: "ms",
            description: "Duration of cache operations");

        _embeddingDuration = _meter.CreateHistogram<double>(
            "rag_embedding_duration",
            unit: "ms",
            description: "Duration of embedding operations");

        _chromaDBDuration = _meter.CreateHistogram<double>(
            "rag_chromadb_duration",
            unit: "ms",
            description: "Duration of ChromaDB operations");

        _queryProcessingDuration = _meter.CreateHistogram<double>(
            "rag_query_processing_duration",
            unit: "ms",
            description: "Duration of query processing");

        _modelInferenceDuration = _meter.CreateHistogram<double>(
            "rag_model_inference_duration",
            unit: "ms",
            description: "Duration of model inference");
    }

    public void RecordCacheOperation(string operation, long durationMs, bool success)
    {
        try
        {
            var tags = new TagList
            {
                { "operation", operation },
                { "success", success.ToString() }
            };

            _cacheDuration.Record(durationMs, tags);

            _logger.LogDebug(
                "Cache operation: {Operation}, Duration: {Duration}ms, Success: {Success}",
                operation, durationMs, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record cache metrics");
        }
    }

    public void RecordCacheError(string operation)
    {
        try
        {
            _cacheErrors.Add(1, new TagList { { "operation", operation } });
            _logger.LogWarning("Cache error recorded for operation: {Operation}", operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record cache error metrics");
        }
    }

    public void RecordEmbeddingOperation(long durationMs, int tokenCount, bool success)
    {
        try
        {
            var tags = new TagList
            {
                { "success", success.ToString() },
                { "token_count", tokenCount.ToString() }
            };

            _embeddingDuration.Record(durationMs, tags);

            _logger.LogDebug(
                "Embedding operation: Duration: {Duration}ms, Tokens: {Tokens}, Success: {Success}",
                durationMs, tokenCount, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record embedding metrics");
        }
    }

    public void RecordChromaDBOperation(string operation, long durationMs, bool success)
    {
        try
        {
            var tags = new TagList
            {
                { "operation", operation },
                { "success", success.ToString() }
            };

            _chromaDBDuration.Record(durationMs, tags);

            _logger.LogDebug(
                "ChromaDB operation: {Operation}, Duration: {Duration}ms, Success: {Success}",
                operation, durationMs, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record ChromaDB metrics");
        }
    }

    public void RecordQueryProcessing(long durationMs, int chunkCount, bool success)
    {
        try
        {
            var tags = new TagList
            {
                { "success", success.ToString() },
                { "chunk_count", chunkCount.ToString() }
            };

            _queryProcessingDuration.Record(durationMs, tags);

            _logger.LogDebug(
                "Query processing: Duration: {Duration}ms, Chunks: {Chunks}, Success: {Success}",
                durationMs, chunkCount, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record query processing metrics");
        }
    }

    public void RecordModelInference(string model, long durationMs, int tokenCount, bool success)
    {
        try
        {
            var tags = new TagList
            {
                { "model", model },
                { "success", success.ToString() },
                { "token_count", tokenCount.ToString() }
            };

            _modelInferenceDuration.Record(durationMs, tags);

            _logger.LogDebug(
                "Model inference: {Model}, Duration: {Duration}ms, Tokens: {Tokens}, Success: {Success}",
                model, durationMs, tokenCount, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record model inference metrics");
        }
    }
}