# Decision records

| Record | Date | Status | Decision |
| --- | --- | --- | --- |
| [0001](0001-local-bootstrap.md) | 2026-09-18 | Accepted for repository setup | Local context repository; remote and implementation choices remain open |
| [0002](0002-mvp-foundation.md) | 2026-09-19 | Implementation sequencing | Working MVP and tested local-media foundation; export and host integration follow |
| [0003](0003-bounded-export.md) | 2026-09-19 | Implemented feasibility slice | Caption/edit/preflight/export APIs; bounded copy/encode matrix and decoded-content validation |
| [0004](0004-stdio-mcp-and-jobs.md) | 2026-09-19 | Accepted for local MVP | Official SDK stdio host, workspace isolation, image content/import and durable export jobs |
| [0005](0005-timed-image-rendering.md) | 2026-09-19 | Accepted for bounded local MVP | Revision-aware timed PNG preview and validated lossless image/silence rendering |
| [0006](0006-acquisition-caption-speech-boundary.md) | 2026-09-19 | Deterministic foundation accepted | Bounded yt-dlp staging, caption evidence/override and provider-neutral timed local-STT chunks |
| [0007](0007-live-acquisition-and-whisper.md) | 2026-09-19 | Accepted for bounded Windows MVP | Explicit Deno acquisition and pinned timed Whisper.net base.en provider |
| [0008](0008-evidence-backed-analysis.md) | 2026-09-19 | Accepted for bounded MCP/CLI MVP | Persist evidence before conservative revision-bound editorial proposals |
| [0009](0009-reversible-speaker-and-voice-boundary.md) | 2026-09-19 | Accepted for bounded CLI/MCP foundation | Revisioned speaker corrections and provenance-rich reversible Qwen preview import |
| [0010](0010-loopback-qwen-provider.md) | 2026-09-19 | Accepted for bounded local MVP | Loopback-only Qwen synthesis through a measured WSL/CUDA service |
| [0011](0011-bounded-voice-rendering.md) | 2026-09-19 | Accepted for bounded local MVP | Explicit 0.8x–1.25x fitting and sample-validated isolated-dialogue replacement export |
| [0012](0012-stable-diarization-boundary.md) | 2026-09-19 | Accepted for bounded CLI/MCP foundation | Stable provider cluster mappings, correction preservation and explicit overlap/unknown assignments |
| [0013](0013-local-sherpa-diarization.md) | 2026-09-19 | Accepted for bounded Windows MVP | Optional sherpa-onnx runtime behind dedicated CLI and MCP configuration |
| [0014](0014-cupri-desktop-review.md) | 2026-09-19 | Accepted for initial Windows review slice | CupriFace shell with exact revision-aware FFmpeg frames and persisted speaker undo/redo |

Add numbered records with context, decision, status, consequences, evidence and review trigger. Supersede previous decisions rather than silently rewriting history. Product proposals in the brief are not accepted decisions.
