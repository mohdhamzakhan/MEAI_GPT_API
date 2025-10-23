using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class RagController : ControllerBase
    {
        private readonly IRAGService _ragService;
        private readonly ILogger<RagController> _logger;
        private readonly DynamicCodingAssistanceService _codingService; // Add this
        private readonly CodingDetectionService _codingDetection;
        private readonly DynamicRAGInitializationService _initService;


        [ActivatorUtilitiesConstructor]
        public RagController(
        IRAGService ragService,
        DynamicCodingAssistanceService codingService, // Inject coding service
        CodingDetectionService codingDetection,
        DynamicRAGInitializationService initService,
        ILogger<RagController> logger)
        {
            _ragService = ragService;
            _codingService = codingService;
            _logger = logger;
            _codingDetection = codingDetection;
            _initService = initService;
        }
        [HttpPost("query")]
        public async Task<IActionResult> Query([FromBody] QueryRequest request, [FromServices] IBackgroundTaskQueue taskQueue)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty");

            var tcs = new TaskCompletionSource<QueryResponse>();

            await taskQueue.QueueBackgroundWorkItemAsync(async token =>
            {
                try
                {
                    // 🔍 DETECT IF IT'S A CODING QUERY
                    var codingDetection = _codingDetection.DetectCodingQuery(request.Question);

                    QueryResponse finalResponse;

                    if (codingDetection.IsCodingRelated && codingDetection.Confidence > 0.5)
                    {
                        var codingResponse = await _codingService.ProcessCodingQueryAsync(
                            request.Question,
                            null,
                            codingDetection.DetectedLanguage != "general" ? codingDetection.DetectedLanguage : null,
                            request.sessionId,
                            includeExamples: true,
                            difficulty: "intermediate"
                        );

                        finalResponse = new QueryResponse
                        {
                            Answer = codingResponse.Solution,
                            Sources = new List<string> { $"{codingResponse.Language} Coding Assistant" },
                            SessionId = codingResponse.SessionId,
                            ProcessingTimeMs = codingResponse.ProcessingTimeMs,
                            IsFromCache = codingResponse.IsFromCache,
                            Confidence = codingResponse.Confidence,
                            Metadata = new Dictionary<string, object>
                            {
                                ["IsCodingResponse"] = true,
                                ["Language"] = codingResponse.Language,
                                ["TechnicalLevel"] = codingResponse.TechnicalLevel,
                                ["SolutionComplexity"] = codingResponse.SolutionComplexity,
                                ["CodeExamples"] = codingResponse.CodeExamples,
                                ["RecommendedNextSteps"] = codingResponse.RecommendedNextSteps,
                                ["RelatedTopics"] = codingResponse.RelatedTopics
                            }
                        };
                    }
                    else
                    {
                        finalResponse = await _ragService.ProcessQueryAsync(
                            request.Question,
                            request.Plant,
                            request.GenerationModel,
                            request.EmbeddingModel,
                            request.MaxResults,
                            request.meai_info,
                            request.sessionId,
                            useReRanking: true
                        );
                    }

                    tcs.SetResult(finalResponse);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Query processing failed.");
                    tcs.SetException(ex);
                }
            });

            // Wait until background worker finishes processing this job
            var result = await tcs.Task;
            return Ok(result);
        }

        [HttpPost("query-stream")]
        public async Task QueryStream([FromBody] QueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await Response.WriteAsync("Question cannot be empty");
                return;
            }

            // ✅ FIXED: Correct headers for Server-Sent Events
            Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["X-Accel-Buffering"] = "no";

            var ct = HttpContext.RequestAborted;

            try
            {
                await SendSSEEvent("status", "Processing your query...", ct);

                var codingDetection = _codingDetection.DetectCodingQuery(request.Question);

                if (codingDetection.IsCodingRelated)
                {
                    await SendSSEEvent("status", $"Detected {codingDetection.DetectedLanguage} coding query", ct);
                }

                await foreach (var chunk in ProcessQueryStreamAsync(request, codingDetection).WithCancellation(ct))
                {
                    if (ct.IsCancellationRequested) break;
                    await SendSSEEvent("data", chunk, ct);
                }

                await SendSSEEvent("status", "Complete", ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Client disconnected during streaming");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during streaming");
                try
                {
                    await SendSSEEvent("error", ex.Message, CancellationToken.None);
                }
                catch { }
            }
        }

        // ✅ FIXED: Proper SSE format
        private async Task SendSSEEvent(string eventType, string data, CancellationToken ct)
        {
            var sseData = eventType switch
            {
                "data" => $"data: {data}\n\n",
                "status" => $"event: status\ndata: {data}\n\n",
                "error" => $"event: error\ndata: {data}\n\n",
                _ => $"event: {eventType}\ndata: {data}\n\n"
            };

            await Response.WriteAsync(sseData, ct);
            await Response.Body.FlushAsync(ct);
        }

        private async Task SendStreamEvent(object payload, CancellationToken ct)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await Response.WriteAsync($"{json}\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        private async IAsyncEnumerable<string> ProcessCodingQueryStreamAsync(QueryRequest request, CodingDetectionResult codingDetection)
        {
            yield return "Analyzing coding requirements...";
            yield return $"Detected {codingDetection.DetectedLanguage} programming query";

            // Handle coding service call safely outside yield context
            CodingAssistanceResponse codingResponse = null;
            var hasError = false;

            var codingTask = _codingService.ProcessCodingQueryAsync(
                request.Question,
                null,
                codingDetection.DetectedLanguage != "general" ? codingDetection.DetectedLanguage : null,
                request.sessionId,
                includeExamples: true,
                difficulty: "intermediate"
            );

            var timeoutTask = Task.Delay(25000);
            var completedTask = await Task.WhenAny(codingTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                hasError = true;
            }
            else
            {
                if (codingTask.IsFaulted)
                {
                    hasError = true;
                    _logger.LogError(codingTask.Exception, "Coding service failed");
                }
                else
                {
                    codingResponse = await codingTask;
                }
            }

            if (hasError || codingResponse == null || string.IsNullOrEmpty(codingResponse.Solution))
            {
                yield return "I apologize, but I couldn't generate a coding solution at this time.";
                yield break;
            }

            yield return "Generating coding solution...";

            await foreach (var chunk in StreamCodeSolution(codingResponse.Solution))
            {
                yield return chunk;
            }
        }
        private async IAsyncEnumerable<string> StreamResponseSafely(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                yield break;

            var sentences = response.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var sentence in sentences)
            {
                var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var currentChunk = new StringBuilder();

                for (int i = 0; i < words.Length; i++)
                {
                    currentChunk.Append(words[i] + " ");

                    // Send chunk every 8-10 words or at sentence end
                    if ((i + 1) % 9 == 0 || i == words.Length - 1)
                    {
                        var chunk = currentChunk.ToString().Trim();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            yield return chunk + " ";
                            currentChunk.Clear();
                            await Task.Delay(70);
                        }
                    }
                }

                // Add sentence ending if not present
                if (!sentence.EndsWith(".") && !sentence.EndsWith("!") && !sentence.EndsWith("?"))
                {
                    yield return ". ";
                }

                await Task.Delay(100);
            }
        }

        private async IAsyncEnumerable<string> StreamCodeSolution(string solution)
        {
            if (string.IsNullOrWhiteSpace(solution))
                yield break;

            var codeBlockPattern = @"```[\w]*\n(.*?)\n```";
            var matches = System.Text.RegularExpressions.Regex.Matches(solution, codeBlockPattern,
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (matches.Count > 0)
            {
                var lastIndex = 0;

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    // Stream text before code block
                    var textBefore = solution.Substring(lastIndex, match.Index - lastIndex);
                    if (!string.IsNullOrWhiteSpace(textBefore))
                    {
                        await foreach (var chunk in StreamResponseSafely(textBefore.Trim()))
                        {
                            yield return chunk;
                        }
                    }

                    yield return "\n\n```" + GetLanguageFromCodeBlock(match.Value) + "\n";

                    // Stream code line by line
                    var code = match.Groups[1].Value;
                    var codeLines = code.Split('\n');

                    foreach (var line in codeLines)
                    {
                        yield return line + "\n";
                        await Task.Delay(80);
                    }

                    yield return "```\n\n";
                    lastIndex = match.Index + match.Length;
                }

                // Stream remaining text
                if (lastIndex < solution.Length)
                {
                    var remainingText = solution.Substring(lastIndex);
                    await foreach (var chunk in StreamResponseSafely(remainingText.Trim()))
                    {
                        yield return chunk;
                    }
                }
            }
            else
            {
                await foreach (var chunk in StreamResponseSafely(solution))
                {
                    yield return chunk;
                }
            }
        }
        private async IAsyncEnumerable<string> StreamTextByWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            var sentences = text.Split(new[] { ". ", "! ", "? ", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var sentence in sentences)
            {
                var words = sentence.Split(' ');
                var currentChunk = new StringBuilder();

                for (int i = 0; i < words.Length; i++)
                {
                    currentChunk.Append(words[i] + " ");

                    // Send chunk every 8-12 words or at punctuation
                    if ((i + 1) % 10 == 0 ||
                        words[i].EndsWith(".") ||
                        words[i].EndsWith("!") ||
                        words[i].EndsWith("?") ||
                        words[i].EndsWith(":"))
                    {
                        yield return currentChunk.ToString();
                        currentChunk.Clear();
                        await Task.Delay(60);
                    }
                }

                // Send any remaining content
                if (currentChunk.Length > 0)
                {
                    yield return currentChunk.ToString();
                }

                // Add sentence ending if not already there
                if (!sentence.EndsWith(".") && !sentence.EndsWith("!") && !sentence.EndsWith("?"))
                {
                    yield return ". ";
                }

                await Task.Delay(120); // Pause between sentences
            }
        }
        private string GetLanguageFromCodeBlock(string codeBlock)
        {
            var lines = codeBlock.Split('\n');
            if (lines.Length > 0)
            {
                var firstLine = lines[0].Trim();
                if (firstLine.StartsWith("```"))
                {
                    return firstLine.Substring(3).Trim();
                }
            }
            return "";
        }
        private async IAsyncEnumerable<string> ProcessQueryStreamAsync(QueryRequest request, CodingDetectionResult codingDetection)
        {
            if (codingDetection.IsCodingRelated && codingDetection.Confidence > 0.5)
            {
                await foreach (var chunk in ProcessCodingQueryStreamAsync(request, codingDetection))
                {
                    yield return chunk;
                }
            }
            else
            {
                await foreach (var chunk in ProcessRAGQueryStreamAsync(request))
                {
                    yield return chunk;
                }
            }
        }

        private async IAsyncEnumerable<string> ProcessRAGQueryStreamAsync(
     QueryRequest request,
     [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var streamChunk in _ragService.ProcessQueryStreamAsync(
                request.Question,
                request.Plant,
                request.GenerationModel,
                request.EmbeddingModel,
                request.MaxResults,
                request.meai_info,
                request.sessionId,
                useReRanking: true,
                ct))
            {
                if (ct.IsCancellationRequested) yield break;

                // ✅ FIXED: Convert StreamChunk to proper JSON string
                var jsonPayload = streamChunk.Type switch
                {
                    "status" => JsonSerializer.Serialize(new { type = "status", message = streamChunk.Content }),
                    "chunk" => JsonSerializer.Serialize(new
                    {
                        type = "chunk",
                        text_preview = streamChunk.TextPreview ?? streamChunk.Content,
                        source = streamChunk.Source,
                        similarity = streamChunk.Similarity
                    }),
                    "sources" => JsonSerializer.Serialize(new
                    {
                        type = "sources",
                        message = streamChunk.Content,
                        sources = streamChunk.Sources
                    }),
                    "response" => JsonSerializer.Serialize(new { type = "response", content = streamChunk.Content }),
                    "error" => JsonSerializer.Serialize(new { type = "error", message = streamChunk.Content }),
                    "complete" => JsonSerializer.Serialize(new { type = "complete", message = "Processing complete" }),
                    _ => JsonSerializer.Serialize(new { type = streamChunk.Type, content = streamChunk.Content })
                };

                yield return jsonPayload;
            }
        }


        [HttpPost("query-with-files")]
        public async Task QueryWithFiles()
        {
            var form = await Request.ReadFormAsync();
            var question = form["question"].ToString();
            var model = form["model"].ToString();
            var plant = form["plant"].ToString();
            var meaiInfo = bool.Parse(form["meai_info"].ToString());
            var sessionId = form["sessionId"].ToString();
            var files = form.Files;

            Response.Headers["Content-Type"] = "text/plain; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["X-Accel-Buffering"] = "no";

            var ct = HttpContext.RequestAborted;

            try
            {
                await SendStreamEvent(new { type = "status", message = "Processing files..." }, ct);

                // Process files first
                var fileContents = new List<string>();
                foreach (var file in files)
                {
                    if (file.Length > 0)
                    {
                        using var reader = new StreamReader(file.OpenReadStream());
                        var content = await reader.ReadToEndAsync();
                        fileContents.Add($"File: {file.FileName}\n{content}");
                    }
                }

                // Add file content to question
                if (fileContents.Any())
                {
                    question = $"{question}\n\nFile Contents:\n{string.Join("\n\n", fileContents)}";
                }

                var queryRequest = new QueryRequest
                {
                    Question = question,
                    Plant = plant,
                    GenerationModel = model,
                    EmbeddingModel = "nomic-embed-text:v1.5",
                    MaxResults = 10,
                    meai_info = meaiInfo,
                    sessionId = sessionId
                };

                await foreach (var chunk in ProcessRAGQueryStreamAsync(queryRequest).WithCancellation(ct))
                {
                    if (ct.IsCancellationRequested) break;
                    await SendStreamEvent(new { type = "llm", fragment = chunk }, ct);
                }

                await SendStreamEvent(new { type = "status", message = "Complete" }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing files");
                await SendStreamEvent(new { type = "error", message = ex.Message }, ct);
            }
        }

        [HttpPost("initialize-session")]
        public async Task<ActionResult<InitializationResult>> InitializeSession([FromBody] InitializeSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest("UserId is required");

            try
            {
                var result = await _initService.InitializeUserSessionAsync(request.UserId, request.SessionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session initialization failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("initialization-progress")]
        public async Task<ActionResult<List<InitializationProgressUpdate>>> GetInitializationProgress()
        {
            try
            {
                var progress = await _initService.GetInitializationProgressAsync();
                return Ok(progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get initialization progress");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("system-ready")]
        public async Task<ActionResult<bool>> IsSystemReady()
        {
            try
            {
                var isReady = await _initService.IsSystemReadyAsync();
                return Ok(new { isReady, timestamp = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check system readiness");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Add request model
        public class InitializeSessionRequest
        {
            public string UserId { get; set; } = string.Empty;
            public string? SessionId { get; set; }
        }
        // Add a dedicated coding endpoint for explicit coding queries
        [HttpPost("coding-query")]
        public async Task<ActionResult<CodingAssistanceResponse>> CodingQuery([FromBody] CodingQueryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question cannot be empty");

            try
            {
                var response = await _codingService.ProcessCodingQueryAsync(
                    codingQuestion: request.Question,
                    codeContext: request.CodeContext,
                    language: request.Language,
                    sessionId: request.SessionId,
                    includeExamples: request.IncludeExamples,
                    difficulty: request.Difficulty ?? "intermediate"
                );

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coding query processing failed: {Question}", request.Question);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("models")]
        public async Task<ActionResult<List<ModelConfiguration>>> GetAvailableModels()
        {
            try
            {
                var models = await _ragService.GetAvailableModelsAsync();
                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available models");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("feedback")]
        public async Task<ActionResult> SubmitFeedback([FromBody] FeedbackRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.CorrectAnswer))
                return BadRequest("Question and correct answer are required");

            try
            {
                await _ragService.ApplyCorrectionAsync(request.sessionId, request.Question, request.CorrectAnswer, request.model);
                return Ok(new { message = "Feedback saved successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("status")]
        public ActionResult<SystemStatus> GetStatus()
        {
            //var status = _ragService.GetSystemStatusAsync();
            //return Ok(status);
            return Ok();
        }

        [HttpPost("refresh-embeddings")]
        public async Task<ActionResult> RefreshEmbeddings()
        {
            try
            {
                await _ragService.InitializeAsync();
                return Ok(new { message = "Embeddings refreshed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("corrections")]
        public ActionResult<List<CorrectionEntry>> GetCorrections([FromQuery] int limit = 50)
        {
            //var corrections = _ragService.GetRecentCorrections(limit);
            //return Ok(corrections);
            return Ok();
        }

        [HttpDelete("corrections/{id}")]
        public async Task<ActionResult> DeleteCorrection(string id)
        {
            try
            {
                //var success = await _ragService.DeleteCorrectionAsync(id);
                //if (!success)
                //    return NotFound("Correction not found");

                return Ok(new { message = "Correction deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("upload-policy")]
        public async Task<ActionResult> UploadPolicy(IFormFile file, string model)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            try
            {
                //await _ragService.ProcessUploadedPolicyAsync(file, model);
                return Ok(new { message = "Policy uploaded and processed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        [HttpPost("feedback/like")]
        public async Task<IActionResult> Like([FromBody] FeedbackRequest feedback)
        {
            await _ragService.MarkAppreciatedAsync(feedback.sessionId, feedback.Question);
            return Ok();
        }

        [HttpGet("rag-status")]
        public async Task<IActionResult> GetRagStatus()
        {
            try
            {
                if (_ragService is DynamicRagService dynamicRag)
                {
                    var isInitialized = dynamicRag._isInitialized; // You'll need to make this public
                    var systemStatus = await dynamicRag.GetSystemStatusAsync();

                    return Ok(new
                    {
                        IsInitialized = systemStatus.IsHealthy,
                        IsHealthy = systemStatus.IsHealthy,
                        Status = isInitialized ? "Ready" : "Initializing",
                        Message = isInitialized ? "RAG system is ready" : "RAG system is still initializing..."
                    });
                }

                return Ok(new { Status = "Unknown", Message = "Unable to determine RAG status" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Status = "Error", Message = ex.Message });
            }
        }

        [HttpGet("delete-model")]
        public async Task<IActionResult> DeleteModelFromChroma(string model)
        {
            await _ragService.DeleteModelDataFromChroma(model);
            return Ok(new { message = $"Model {model} data deleted from ChromaDB" });
        }
    }
}
public class CodingQueryRequest
{
    public string Question { get; set; } = string.Empty;
    public string? CodeContext { get; set; }
    public string? Language { get; set; }
    public string? SessionId { get; set; }
    public bool IncludeExamples { get; set; } = true;
    public string? Difficulty { get; set; } = "intermediate";
}


