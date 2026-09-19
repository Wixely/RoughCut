# RoughCut MVP

- Defined: 2026-09-19
- Status: Working implementation scope; foundation and bounded export implemented, MVP incomplete
- Owner: Implementation agent; Wixely resolves material product/dependency tradeoffs
- Review: After each milestone and before adopting speech or UI dependencies
- Source: User request to identify the MVP and start work

## Smallest useful product

Edit one local video into one output timeline, either through a small desktop review UI or entirely through MCP. An agent can inspect timed speech and actual video frames, propose an edit, bring in generated images, save/reopen the project, and export with an honest copy/encode preflight. Speaker distinction and Qwen TTS replacement remain part of this working MVP scope because they are explicit product requirements.

The agent supplies the initial interpretation of a natural-language prompt through MCP. This avoids selecting an embedded semantic model before the deterministic media operations work. It does not authorize sending media to an external model automatically. Standalone prompt interpretation remains a later integration choice.

## Bounded acceptance

| Capability | MVP boundary and completion gate |
| --- | --- |
| Project and edits | One local source video; hard cuts, trim/split/remove/reorder, fixed crop per clip, versioned portable JSON, source fingerprints, revisions and reversible edits |
| Speech | Import supplied timed text; local STT fallback with a validated runtime; distinguish and correct speaker labels, with explicit unknown/overlap states |
| Agent vision and images | Retrieve a readable image at a valid source timestamp through MCP; import bounded PNGs generated elsewhere, insert timed stills, preview, save/reopen and export |
| Voice replacement | Qwen TTS for selected speakers/intervals on clean, non-overlapping dialogue; review generated audio and duration fit; preserve originals. Reject unsupported mixed-background/overlap cases explicitly |
| Export | Bounded, recorded codec/container coverage; safe copy where proven, explicit encoding for crop/stills/replacement; strict copy-only rejection when unsupported; decode and check joins and A/V timing |
| Hosts | Shared application jobs, CLI, stdio MCP and minimal desktop review/preview; actual image-capable MCP client verification and cancellation; Windows first, Linux acceptance before claiming Linux support |

General multitrack compositing, transitions, automatic source separation, voice cloning, translation, URL/yt-dlp acquisition, standalone semantic-model selection and hosted web/Docker operation follow this MVP. These sequencing choices do not remove the existing product requirements. No dependency, cloud provider, license or remote host is adopted by this scope document.

## Milestones

| Milestone | Work | Owner | State |
| --- | --- | --- | --- |
| M1: Foundation | Typed project/timing/speech/image contracts, validation, revision saves, local inspection and frame extraction, runnable CLI/debugging | Implementation agent | Initial implementation verified; remaining contract work below |
| M2: Reproducible edit/export | Caption provenance, supplied captions and explicit edits; strict copy feasibility and encoding path with decoded join checks | Implementation agent | Bounded Windows slice verified; broader analysis/synthesis provenance continues in RC-01 |
| M3: Agent workflow | Shared jobs and stdio MCP over tested operations, validated image import/timed stills and timeline preview | Implementation agent | Bounded jobs/MCP/frame/import/insert/preview/export path verified; interactive agent visual interpretation remains RC-10 acceptance |
| M4: Speech and review | Local STT/diarization, bounded Qwen replacement and minimal desktop preview/editor | Implementation agent; Wixely for runtime/UI tradeoffs | RC-03 local Whisper verified; diarization, RC-09 and RC-06 remain |
| M5: Acceptance | Full workflow through MCP and desktop, media regression matrix, published Windows/Linux execution | Implementation agent | RC-07; MVP is not complete until gates pass |

## What works now

The .NET 10 solution contains a UI-independent core, an FFmpeg adapter, CLI, application jobs and a stdio MCP host. The CLI and MCP tools create, validate and revision-save JSON projects, apply edit batches, import SRT/images, inspect local video, retrieve PNG frames, preflight and export a [bounded matrix](export.md). See [development commands](development.md), [MCP evidence](evidence/2026-09-19-mcp.md) and [export evidence](evidence/2026-09-19-export.md).

The current schema is a development baseline. Structured analysis evidence/observations/proposals round-trip and MCP applies revision-bound conservative decisions. Speaker/image fields also round-trip, and MCP can import/insert PNG assets for revision-aware preview and bounded encoded export. Speech synthesis, speaker correction history and future schema migrations still need work. Project validation checks document structure; export preflight additionally validates active source, image and caption files and export capability.

Frame extraction currently indexes and decodes from the beginning, bounded to 100,000 frames, 16 MiB tool output and a 60-second per-tool timeout. This proves timestamp selection before adding cached seek indexes for long videos. It handles tested SDR, unrotated, square-pixel fixtures; rotation, non-square pixels, HDR and changing frame dimensions are rejected. Export has stricter documented limits and validates every retained or rendered frame/sample before publishing. There is a bounded stdio MCP host, PNG importer, timed-image renderer, MCP-supplied analysis mode and local Whisper runtime; there is no GUI, standalone semantic provider, diarization or Qwen TTS runtime yet.

Recommended next action: **Implementation agent: extend speaker correction/synthesis provenance and begin RC-09 diarization/Qwen TTS feasibility; Wixely: provide representative labelled ad-removal and multi-speaker examples.** Broader format and rendering features need equivalent media evidence before acceptance.
