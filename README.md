# RoughCut

AI-assisted video editing from a local video or URL and a natural-language prompt. Produce an editable JSON cut, with Windows/Linux desktop interfaces and complete headless MCP workflows; a Docker-hosted web edition follows later.

Requirements added on **2026-09-19**: distinguish speakers and allow voice replacement using Qwen TTS. These are planned capabilities; backend/runtime evaluation and implementation remain. See the [speech workflow](docs/product-and-architecture.md#speaker-distinction-and-qwen-tts-voice-replacement) and RC-09 in the work queue.

Also required: retrieve video frames at requested timestamps as agent-readable images through MCP, and accept images through MCP for agent-generated content. The stdio host now returns actual PNG content blocks and imports validated PNG assets with provenance. Timed still insertion/rendering remains. See [image exchange](docs/product-and-architecture.md#mcp-frame-retrieval-and-image-inputs) and RC-10.

Status on **2026-09-19**: the .NET 10 CLI and [stdio MCP host](docs/mcp.md) support JSON projects, transactional edits, SRT/image import, video inspection/frame retrieval, export preflight and a [bounded validated export workflow](docs/export.md). Windows verification passes 32 checks; the CLI NativeAOT path is rechecked by the full script. The [MVP](docs/mvp.md) remains incomplete; timed image rendering, speech processing and UI are next milestones. Remote hosting, visibility and license remain undecided; no remote is configured.

## Start here

1. Read [AGENTS.md](AGENTS.md).
2. Read the [next-agent handoff](docs/handoff.md).
3. Read the [product and architecture brief](docs/product-and-architecture.md), including confirmed requirements, assumptions and open decisions.
4. Read the [MVP scope](docs/mvp.md) and execute the [work queue](docs/work-queue.md), continuing RC-10 timed image insertion/rendering.
5. Record evidence using the [validation plan](docs/validation.md) and preserve [decision history](docs/decisions/README.md).

The completed export feasibility slice proves supported copy-only edits, explicit crop encoding and unsupported-cut rejection on synthetic video/audio. Read its format limits before using it with other media. Shared operations and durable export jobs are exposed through stdio MCP; UI and speech runtime choices still require evaluation.

```powershell
dotnet build RoughCut.slnx
dotnet run --project src/RoughCut.Cli -- help
.\scripts\verify.ps1 -PublishAot
```

See [development commands and limits](docs/development.md), repository-local VS Code debug configurations, and [execution evidence](docs/evidence/2026-09-19-foundation.md). FFmpeg/FFprobe must be available separately. Linux and full editing workflows are not yet verified.

This repository owns implementation context from this point forward. [PLAN discovery history](../PLAN/inbox/roughcut.md), [shared preferences](../PLAN/preferences/README.md), and the [dated feasibility research](../PLAN/knowledge/media/video-editing-feasibility-2026-09-18.md) remain in the sibling PLAN repository. These relative links assume both checkouts share a parent directory.

Recommended next action — Owner: Implementation agent: finish RC-10 with revision-aware timed image insertion, timeline preview and encoded export. Richer evidence/speech contracts, UI, voice replacement and broader media/platform acceptance remain.
