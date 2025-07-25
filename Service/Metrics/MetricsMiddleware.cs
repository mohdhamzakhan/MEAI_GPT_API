using OpenTelemetry.Trace;
using System.Diagnostics;

public class MetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMetricsCollector _metrics;

    public MetricsMiddleware(RequestDelegate next, IMetricsCollector metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
            sw.Stop();

            _metrics.RecordChromaDBOperation(
                context.Request.Path,
                context.Response.StatusCode,
                context.Response.StatusCode.ToString().StartsWith('2')?true:false );
        }
        catch (Exception)
        {
            sw.Stop();
            _metrics.RecordCacheError( context.Request.Path);
            throw;
        }
    }
}

// Extension method
public static class MetricsMiddlewareExtensions
{
    public static IApplicationBuilder UseMetrics(
        this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<MetricsMiddleware>();
    }
}