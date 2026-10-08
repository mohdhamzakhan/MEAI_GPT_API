using MEAI_GPT_API.Models;
using MEAI_GPT_API.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using static MEAI_GPT_API.Services.DynamicRagService;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly DynamicRagService _rag;
    private readonly AttachmentProcessor _attachmentProcessor;
    private readonly ILogger<ChatController> _logger;

    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ChatController(
        DynamicRagService rag,
        AttachmentProcessor attachmentProcessor,
        ILogger<ChatController> logger)
    {
        _rag = rag;
        _attachmentProcessor = attachmentProcessor;
        _logger = logger;
    }

    [HttpPost("stream-with-files")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(60_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60_000_000)]
    public async Task StreamWithFiles([FromForm] StreamWithFilesRequest request, CancellationToken ct)
    {
        // ---------- 1. Validate BEFORE the stream starts, so errors are real HTTP 400s ----------
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            await WriteBadRequestAsync("Question is required.", ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(request.Plant))
        {
            await WriteBadRequestAsync("Plant is required.", ct);
            return;
        }

        List<ChatAttachment> attachments;
        try
        {
            attachments = request.Files is { Count: > 0 }
                ? await _attachmentProcessor.ProcessAsync(request.Files)
                : new List<ChatAttachment>();
        }
        catch (ArgumentException ex)
        {
            await WriteBadRequestAsync(ex.Message, ct);
            return;
        }

        // ---------- 2. Open the SSE stream ----------
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // stops nginx from buffering
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var userId = User.Identity?.Name ?? "system";

        try
        {
            await foreach (var chunk in _rag.ProcessQueryStreamWithAttachmentsAsync(
                request.Question,
                request.Plant,
                attachments,
                request.SessionId,
                userId,
                request.MeaiInfo,
                request.MaxResults,
                ct))
            {
                await WriteSseAsync(chunk, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client disconnected during stream-with-files");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "stream-with-files failed");
            if (!ct.IsCancellationRequested)
                await WriteSseAsync(new StreamChunk { Type = "error", Content = "Something went wrong while processing your request." }, CancellationToken.None);
        }
    }

    private async Task WriteSseAsync(StreamChunk chunk, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(chunk, SseJson);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private async Task WriteBadRequestAsync(string message, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new { error = message }, SseJson), ct);
    }
}