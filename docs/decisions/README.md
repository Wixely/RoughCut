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

Add numbered records with context, decision, status, consequences, evidence and review trigger. Supersede previous decisions rather than silently rewriting history. Product proposals in the brief are not accepted decisions.
