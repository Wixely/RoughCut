# RoughCut

AI-assisted video editing from a local video or URL and a natural-language prompt. Produce an editable JSON cut, with Windows/Linux desktop interfaces and complete headless MCP workflows; a Docker-hosted web edition follows later.

Requirements added on **2026-09-19**: distinguish speakers and allow voice replacement using Qwen TTS. These are planned capabilities; backend/runtime evaluation and implementation remain. See the [speech workflow](docs/product-and-architecture.md#speaker-distinction-and-qwen-tts-voice-replacement) and RC-09 in the work queue.

Also required: retrieve video frames at requested timestamps as agent-readable images through MCP, and accept images through MCP for agent-generated content. The stdio host returns actual PNG content blocks, imports validated PNG assets with provenance, inserts timed stills, previews an exact project revision and exports the result through an explicit lossless-encoding policy. See [image exchange](docs/product-and-architecture.md#mcp-frame-retrieval-and-image-inputs) and RC-10.

Status on **2026-09-19**: the .NET 10 CLI and [stdio MCP host](docs/mcp.md) support JSON projects, transactional edits, staged URL acquisition, evidence-backed caption selection, SRT/image import, source and timeline frame retrieval, local Whisper transcription, export preflight and a [bounded validated export workflow](docs/export.md). Windows verification passes 37 checks and the CLI NativeAOT path is rechecked by the full script. RC-03 live acceptance passed with standalone yt-dlp plus Deno and a pinned Whisper.net base.en model; see the [live evidence](docs/evidence/2026-09-19-rc03-live.md). The [MVP](docs/mvp.md) remains incomplete; analysis, speaker distinction/Qwen TTS and UI are next. Remote hosting, visibility and license remain undecided; no remote is configured.

## Start here

1. Read [AGENTS.md](AGENTS.md).
2. Read the [next-agent handoff](docs/handoff.md).
3. Read the [product and architecture brief](docs/product-and-architecture.md), including confirmed requirements, assumptions and open decisions.
4. Read the [MVP scope](docs/mvp.md) and execute the [work queue](docs/work-queue.md), continuing with RC-04 analysis.
5. Record evidence using the [validation plan](docs/validation.md) and preserve [decision history](docs/decisions/README.md).

The completed export feasibility slice proves supported copy-only edits, explicit crop encoding and unsupported-cut rejection on synthetic video/audio. Read its format limits before using it with other media. Shared operations, local STT and durable export jobs are exposed through stdio MCP; diarization, Qwen TTS and UI choices still require evaluation.

```powershell
dotnet build RoughCut.slnx
dotnet run --project src/RoughCut.Cli -- help
.\scripts\verify.ps1 -PublishAot
```

See [development commands and limits](docs/development.md), repository-local VS Code debug configurations, and [execution evidence](docs/evidence/2026-09-19-foundation.md). FFmpeg/FFprobe must be available separately. Linux and full editing workflows are not yet verified.

This repository owns implementation context from this point forward. [PLAN discovery history](../PLAN/inbox/roughcut.md), [shared preferences](../PLAN/preferences/README.md), and the [dated feasibility research](../PLAN/knowledge/media/video-editing-feasibility-2026-09-18.md) remain in the sibling PLAN repository. These relative links assume both checkouts share a parent directory.

Recommended next action — Owner: Implementation agent: implement RC-04 evidence-backed analysis proposals with conservative retain-by-default policy. Owner: Wixely: provide a representative labelled ad-removal example when convenient. Speaker distinction/Qwen TTS, interactive image-client acceptance, UI and broader platform validation remain.
