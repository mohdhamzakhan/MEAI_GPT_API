# MEAI_GPT_API — AI/RAG API Architecture

## Purpose
ASP.NET Core 8 API implementing the MEAI GPT/RAG platform with LLM inference, retrieval, policy/document analysis, conversation storage, agentic workflows, reranking, caching, authentication and observability.

## Stack
.NET 8 / ASP.NET Core, EF Core + SQLite, Ollama, ChromaDB, Redis/in-memory cache, JWT, Serilog, OpenTelemetry, Docker/Linux, PDF/Word processing and Tesseract OCR.

## Architecture
HTTP client -> Controllers -> RAG/application services -> retrieval/routing/reranking -> Ollama + ChromaDB -> conversation/cache/policy data -> response.

## Important Areas
- Program.cs — DI, middleware, authentication, persistence, Ollama/ChromaDB clients, caching, telemetry and hosted services.
- Controller/ — HTTP endpoints.
- Service/ and Services/ — RAG, document processing, policy analysis, model management and agent services.
- Services/Agent/ — planner, executor, verifier and tools.
- Models/ — domain/configuration models.
- Migrations/ — EF Core migrations.
- context/ — RAG/retrieval knowledge and configuration.

## RAG
Queries can pass through intent/topic detection, document routing, vector retrieval, BM25 support, cross-encoder reranking, context construction and Ollama generation. Dynamic RAG configuration controls models and collections.

## Agentic AI
Planner, executor, self-verification and tool components are present. Preserve tool boundaries and validation when changing agent behavior.

## AI Rules
Read this file, Program.cs, the affected controller/service and configuration before modifying behavior. Preserve DI lifetimes because singleton/scoped choices affect state and concurrency. Do not expose secrets or hard-code infrastructure.

## Validation
Build the .NET 8 solution and test affected API/RAG paths. For retrieval changes validate both retrieval relevance and generated answers.