using MEAI_GPT_API.Models;
using MEAI_GPT_API.Service;
using MEAI_GPT_API.Service.Interface;
using MEAI_GPT_API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using static MEAI_GPT_API.Services.DynamicRagService;

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
        private readonly IConversationStorageService _conversationStorage;


        [ActivatorUtilitiesConstructor]
        public RagController(
        IRAGService ragService,
        DynamicCodingAssistanceService codingService, // Inject coding service
        CodingDetectionService codingDetection,
        DynamicRAGInitializationService initService,
         IConversationStorageService conversationStorage,
        ILogger<RagController> logger)
        {
            _ragService = ragService;
            _codingService = codingService;
            _logger = logger;
            _codingDetection = codingDetection;
            _initService = initService;
            _conversationStorage = conversationStorage;
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

            Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["X-Accel-Buffering"] = "no";

            var ct = HttpContext.RequestAborted;

            // ✅ FIX: Pre-detect coding query (with error handling)
            // var codingDetection = SafeDetectCoding(request.Question);

            // ✅ Stream without wrapping the entire block in try-catch
            await foreach (var chunk in ProcessQueryStreamAsync(request).WithCancellation(ct))
            {
                if (ct.IsCancellationRequested) break;

                await WriteSSEData(chunk, ct);
            }
        }

        // ✅ NEW: Safe helper methods
        private CodingDetectionResult SafeDetectCoding(string question)
        {
            try
            {
                return _codingDetection.DetectCodingQuery(question);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Coding detection failed, treating as non-coding query");
                return new CodingDetectionResult
                {
                    IsCodingRelated = false,
                    Confidence = 0,
                    DetectedLanguage = "general"
                };
            }
        }

        private async Task WriteSSEData(string data, CancellationToken ct)
        {
            try
            {
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write SSE data");
                // Don't throw - client may have disconnected
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

        //    private async IAsyncEnumerable<string> ProcessRAGQueryStreamAsync(
        //QueryRequest request,
        //[EnumeratorCancellation] CancellationToken ct = default)
        //    {
        //        await foreach (var streamChunk in _ragService.ProcessQueryStreamAsync(
        //            request.Question,
        //            request.Plant,
        //            request.GenerationModel,
        //            request.EmbeddingModel,
        //            request.MaxResults,
        //            request.meai_info,
        //            request.sessionId,
        //            useReRanking: true,
        //            ct))
        //        {
        //            if (ct.IsCancellationRequested) yield break;

        //            // Convert StreamChunk to JSON string based on type
        //            var jsonPayload = streamChunk.Type?.ToLower() switch
        //            {
        //                "status" => JsonSerializer.Serialize(new
        //                {
        //                    Type = "status",
        //                    Content = streamChunk.Content
        //                }),

        //                "chunk" => JsonSerializer.Serialize(new
        //                {
        //                    Type = "chunk",
        //                    Content = streamChunk.Content,
        //                    TextPreview = streamChunk.TextPreview,
        //                    Source = streamChunk.Source,
        //                    Similarity = streamChunk.Similarity
        //                }),

        //                "sources" => JsonSerializer.Serialize(new
        //                {
        //                    Type = "sources",
        //                    Content = streamChunk.Content,
        //                    Sources = streamChunk.Sources
        //                }),

        //                "response" => JsonSerializer.Serialize(new
        //                {
        //                    Type = "response",
        //                    Content = streamChunk.Content
        //                }),

        //                "error" => JsonSerializer.Serialize(new
        //                {
        //                    Type = "error",
        //                    Content = streamChunk.Content
        //                }),

        //                "complete" => JsonSerializer.Serialize(new
        //                {
        //                    Type = "complete",
        //                    Content = streamChunk.Content,
        //                    ProcessingTimeMs = streamChunk.ProcessingTimeMs
        //                }),

        //                _ => JsonSerializer.Serialize(new
        //                {
        //                    Type = streamChunk.Type,
        //                    Content = streamChunk.Content
        //                })
        //            };

        //            yield return jsonPayload;
        //        }
        //    }

        private async Task SendStreamEvent(object payload, CancellationToken ct)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await Response.WriteAsync($"{json}\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        private async IAsyncEnumerable<string> ProcessCodingQueryStreamAsync(
    QueryRequest request,
    CodingDetectionResult codingDetection)
        {
            yield return JsonSerializer.Serialize(new { type = "status", message = "Analyzing coding requirements..." });
            yield return JsonSerializer.Serialize(new { type = "status", message = $"Detected {codingDetection.DetectedLanguage} programming query" });

            // ✅ FIX: Separate the risky operation from the yielding part
            var codingResult = await SafeGetCodingResponse(request, codingDetection);

            if (!codingResult.Success)
            {
                yield return JsonSerializer.Serialize(new { type = "error", message = codingResult.ErrorMessage });
                yield break;
            }

            yield return JsonSerializer.Serialize(new { type = "status", message = "Streaming solution..." });

            // ✅ Now safely stream the solution
            await foreach (var chunk in StreamCodeSolutionSafely(codingResult.Response!.Solution))
            {
                yield return JsonSerializer.Serialize(new { type = "response", content = chunk });
            }

            // Send metadata
            if (codingResult.Response!.CodeExamples?.Any() == true)
            {
                yield return JsonSerializer.Serialize(new
                {
                    type = "metadata",
                    examples = codingResult.Response.CodeExamples,
                    language = codingResult.Response.Language
                });
            }

            yield return JsonSerializer.Serialize(new { type = "complete", message = "Done" });
        }

        // ✅ NEW: Safe wrapper method (can use try-catch)
        private async Task<CodingServiceResult> SafeGetCodingResponse(
            QueryRequest request,
            CodingDetectionResult codingDetection)
        {
            try
            {
                _logger.LogInformation($"🔍 Calling coding service for: {request.Question}");

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
                    _logger.LogError("❌ Coding service timeout");
                    return new CodingServiceResult
                    {
                        Success = false,
                        ErrorMessage = "Coding service timed out after 25 seconds"
                    };
                }

                if (codingTask.IsFaulted)
                {
                    var ex = codingTask.Exception?.InnerException ?? codingTask.Exception;
                    _logger.LogError(ex, "❌ Coding service failed");
                    return new CodingServiceResult
                    {
                        Success = false,
                        ErrorMessage = $"Coding service error: {ex?.Message}"
                    };
                }

                var response = await codingTask;

                if (response == null || string.IsNullOrEmpty(response.Solution))
                {
                    _logger.LogError("❌ Empty response from coding service");
                    return new CodingServiceResult
                    {
                        Success = false,
                        ErrorMessage = "Coding service returned empty solution"
                    };
                }

                _logger.LogInformation($"✅ Got solution ({response.Solution.Length} chars)");
                return new CodingServiceResult
                {
                    Success = true,
                    Response = response
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception in coding service");
                return new CodingServiceResult
                {
                    Success = false,
                    ErrorMessage = $"Unexpected error: {ex.Message}"
                };
            }
        }

        // ✅ NEW: Safe streaming method (no try-catch)
        private async IAsyncEnumerable<string> StreamCodeSolutionSafely(string solution)
        {
            if (string.IsNullOrEmpty(solution))
            {
                _logger.LogWarning("❌ Empty solution provided");
                yield break;
            }

            var lines = solution.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                yield return line + "\n";
                await Task.Delay(30);
            }
        }

        // ✅ NEW: Result wrapper class
        private class CodingServiceResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = "";
            public CodingAssistanceResponse? Response { get; set; }
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
            if (string.IsNullOrEmpty(solution))
            {
                _logger.LogWarning("❌ StreamCodeSolution called with empty solution");
                yield break;
            }

            _logger.LogInformation($"📤 Streaming solution: {solution.Length} characters");

            // Option 1: Stream character by character (slower but shows progress)
            const int chunkSize = 50; // characters per chunk
            for (int i = 0; i < solution.Length; i += chunkSize)
            {
                var chunk = solution.Substring(i, Math.Min(chunkSize, solution.Length - i));
                yield return chunk;
                await Task.Delay(20); // Small delay for streaming effect
            }

            // OR Option 2: Stream line by line (better for code)
            // var lines = solution.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            // foreach (var line in lines)
            // {
            //     yield return line + "\n";
            //     await Task.Delay(50);
            // }
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

        private async IAsyncEnumerable<string> ProcessQueryStreamAsync(QueryRequest request)
        {
            await foreach (var chunk in ProcessRAGQueryStreamAsync(request))
            {
                yield return chunk;
            }
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
            // ✅ FIX: Don't wrap the entire method in try-catch
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

                // Convert StreamChunk to JSON
                var jsonPayload = ConvertChunkToJson(streamChunk);
                yield return jsonPayload;
            }
        }

        // ✅ FIXED: Use explicit DTO classes instead of anonymous types
        private string ConvertChunkToJson(StreamChunk streamChunk)
        {
            try
            {
                // Use JsonNamingPolicy to ensure lowercase property names
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };

                return streamChunk.Type switch
                {
                    "status" => JsonSerializer.Serialize(new StatusEventDto
                    {
                        Type = "status",
                        Message = streamChunk.Content
                    }, options),

                    "chunk" => JsonSerializer.Serialize(new ChunkEventDto
                    {
                        Type = "chunk",
                        TextPreview = streamChunk.TextPreview ?? streamChunk.Content,
                        Source = streamChunk.Source,
                        Similarity = streamChunk.Similarity
                    }, options),

                    "sources" => JsonSerializer.Serialize(new SourcesEventDto
                    {
                        Type = "sources",
                        Message = streamChunk.Content,
                        Sources = streamChunk.Sources ?? new List<string>()
                    }, options),

                    "response" => JsonSerializer.Serialize(new ResponseEventDto
                    {
                        Type = "response",
                        Content = streamChunk.Content
                    }, options),

                    "error" => JsonSerializer.Serialize(new ErrorEventDto
                    {
                        Type = "error",
                        Message = streamChunk.Content
                    }, options),

                    "complete" => JsonSerializer.Serialize(new CompleteEventDto
                    {
                        Type = "complete",
                        Message = "Processing complete",
                        ProcessingTimeMs = streamChunk.ProcessingTimeMs
                    }, options),

                    _ => JsonSerializer.Serialize(new GenericEventDto
                    {
                        Type = streamChunk.Type,
                        Content = streamChunk.Content
                    }, options)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serialize chunk");

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                return JsonSerializer.Serialize(new ErrorEventDto
                {
                    Type = "error",
                    Message = "Serialization error"
                }, options);
            }
        }

        // ✅ ADD: DTO classes at the end of your RagController.cs file (before the closing brace)

        public class StatusEventDto
        {
            public string Type { get; set; } = "";
            public string Message { get; set; } = "";
        }

        public class ChunkEventDto
        {
            public string Type { get; set; } = "";
            public string? TextPreview { get; set; }
            public string? Source { get; set; }
            public double? Similarity { get; set; }
        }

        public class SourcesEventDto
        {
            public string Type { get; set; } = "";
            public string Message { get; set; } = "";
            public List<string> Sources { get; set; } = new();
        }

        public class ResponseEventDto
        {
            public string Type { get; set; } = "";
            public string? Content { get; set; }
        }

        public class ErrorEventDto
        {
            public string Type { get; set; } = "";
            public string Message { get; set; } = "";
        }

        public class CompleteEventDto
        {
            public string Type { get; set; } = "";
            public string Message { get; set; } = "";
            public long? ProcessingTimeMs { get; set; }
        }

        public class GenericEventDto
        {
            public string Type { get; set; } = "";
            public string? Content { get; set; }
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
        // Add this to your RAG Controller


        // Add endpoint to force reprocess a specific file
        [HttpPost("diagnostics/reprocess-file")]
        public async Task<ActionResult> ReprocessFile([FromBody] ReprocessFileRequest request)
        {
            try
            {
                _logger.LogInformation($"Forcing reprocess of file: {request.FilePath} for plant: {request.Plant}");

                // Clear cache for this file
                await _ragService.ClearFileCacheAsync(request.FilePath);

                // Reprocess
                await _ragService.ProcessSingleFileAsync(request.FilePath, request.Plant);

                return Ok(new { message = "File reprocessed successfully", file = request.FilePath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reprocess file: {request.FilePath}");
                return StatusCode(500, new { error = "Failed to reprocess file", details = ex.Message });
            }
        }

        [HttpGet("sessions")]
        public async Task<ActionResult<List<SessionSummary>>> GetAllSessions([FromQuery] string? userId = null)
        {
            try
            {
                var sessions = await _conversationStorage.GetConversationSessionsAsync(userId);
                return Ok(sessions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve sessions");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("sessions/{sessionId}/messages")]
        public async Task<ActionResult<List<ConversationMessage>>> GetSessionMessages(string sessionId)
        {
            try
            {
                var messages = await _conversationStorage.GetSessionMessagesAsync(sessionId);
                return Ok(messages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve session messages");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpDelete("sessions/{sessionId}")]
        public async Task<IActionResult> DeleteSession(string sessionId)
        {
            try
            {
                await _conversationStorage.DeleteSessionAsync(sessionId);
                return Ok(new { message = "Session deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete session");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Add these DTOs at the end of the file
        public class SessionSummary
        {
            public string SessionId { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime LastAccessedAt { get; set; }
            public int MessageCount { get; set; }
            public string LastMessage { get; set; } = string.Empty;
            public string? UserId { get; set; }
        }

        public class ConversationMessage
        {
            public string Question { get; set; } = string.Empty;
            public string Answer { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
        }


        public class ReprocessFileRequest
        {
            public string FilePath { get; set; } = "";
            public string Plant { get; set; } = "";
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


