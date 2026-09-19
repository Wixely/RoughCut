# Next-agent handoff

- Updated: 2026-09-19
- Review: At the start of implementation and before dependency adoption
- Owner: Implementation agent

## Current state

The [working MVP](mvp.md) now has shared .NET 10 core/media/application APIs, CLI commands and a [stdio MCP host](mcp.md) for projects, revision-safe edits, bounded live acquisition/caption selection, local Whisper transcription, provider-neutral [evidence-backed analysis](analysis.md), revisioned [speaker corrections and reversible voice previews](speaker-and-voice.md), supplied SRT/PNG import, source/timeline frame retrieval, timed image insertion and durable [bounded validated export](export.md) jobs. Windows managed and NativeAOT-CLI verification each pass 42 checks; see the [speaker/voice evidence](evidence/2026-09-19-speaker-voice-foundation.md). There is no GUI, standalone semantic provider, diarization, direct Qwen runtime or voice rendering yet. No remote or project license is selected.

The [implementation brief](product-and-architecture.md) carries forward the complete discovery plan, including requirements, architecture boundaries, illustrative JSON, export semantics, reuse candidates, open questions and risks. The original [PLAN note](../../PLAN/inbox/roughcut.md) is historical discovery context; maintain new implementation decisions here.

## Continue with live Qwen and diarization feasibility (RC-09)

On 2026-09-19 the user added speaker distinction and voice replacement via Qwen TTS. The bounded RC-01 foundation now persists speaker add/rename/assign/merge history and complete Qwen preview provenance; CLI/MCP can import, return, apply and revert exact PCM WAVE previews. No diarization backend, Qwen model/runtime or deployment mode has been adopted or tested. RC-09 must add live providers, cancellation and eventual rendering without weakening the overlap/background/duration rejections. Strict copy-only export rejects applied voice replacements.

The user also required images retrieved from any valid video timestamp for agents to inspect through MCP, and incoming images through MCP for agent-generated content. The official MCP client now receives/decodes actual PNG content blocks with precise timing, imports bounded checksummed PNG assets with provenance, inserts them transactionally, previews the exact saved revision and completes validated encoded export. Interactive AI-agent visual interpretation has not been tested.

The core now includes caption provenance/retiming, transactional edit batches and export plan/report contracts. The exporter proves copy-only PNG/PCM Matroska cuts, rejects unsupported strict requests, encodes crop/H.264 input when explicitly permitted, and validates every decoded frame, PCM sample, presentation timestamp and copied packet payload before publishing an atomic bundle. It is deliberately limited to the matrix in the export guide. Do not expand safe-copy claims from this one matrix.

The bounded RC-04 workflow accepts source-time evidence/observations through `IContentAnalyzer`, CLI or MCP, records provider/model/prompt/source provenance, maps observations to clips and persists revision-bound proposals. `review` never auto-removes; `auto-high-certainty` removes only a high-certainty provider recommendation. Lower certainty remains review-only. Automatic and explicit reviewed application preserve surrounding material and clear stale proposals. The tests use labelled synthetic ad/no-ad/uncertain observations; no standalone model or real-media quality claim is made.

The bounded yt-dlp adapter, caption assessment/override and timed local-STT processor remain verified. Live Windows acceptance used standalone yt-dlp 2026.07.04 with pinned Deno 2.9.7. Whisper.net/runtime 1.9.1 transcribed a 77.1-second synthetic source in three chunks. Bantz remains unchanged because its public result discards segment times.

Extend the synthetic fixtures and executable test harness without personal media or downloads. Keep an explicit allow/reject policy so it will work through MCP without an interactive dialog. Build/run instructions and VS Code configurations now exist; keep them aligned with changes.

VS Code has 27 launch options covering the stdio MCP host, current CLI commands including analysis save/automatic apply, speaker and voice preview operations, acquisition/caption assessment/timeline preview, Whisper model preparation/transcription, separate exports with/without encoding, and contract/protocol checks. All use the explicit Debug build task. See the development guide for F5 usage, extension requirements and input defaults. Interactive debugger behavior remains unverified.

## Local environment observations

Read-only checks on 2026-09-18 found .NET SDK 10.0.300 (also 8.0.419), Git 2.53.0.windows.2, and FFmpeg/FFprobe executables resolvable on PATH. Their versions, codecs and execution behavior were not validated. `yt-dlp` was not found on PATH; it may exist elsewhere. No tools, runtimes, models or packages were installed. Recheck at use time; absence from PATH is not proof of absence from the machine.

On 2026-09-19, SDK 10.0.300 and FFmpeg/FFprobe `2026-04-01-git-eedf8f0165-full_build` were executed. Windows x64 NativeAOT publishing and the CLI command checks passed. The main CLI stays independent of native Whisper and remains the NativeAOT target; speech CLI and MCP are normal .NET deployments. The current media adapter intentionally scans from the beginning with bounded output/time/frame count; it is not ready for arbitrary long videos. See the evidence for exact fixture coverage and rejected display formats.

The same toolchain passed the export regression suite. The fixture uses font-free visible frame IDs because font-based drawtext crashed this FFmpeg build. Copy selection scans from the beginning to avoid keyframe-seek audio preroll; do not reintroduce input-side seeking without the H.264/audio-copy regression. No Linux, disk-full or crash-durability result is claimed.

Sibling Bantz, CupriFace, DNAX, MCPSharp and MCPHub directories were observed. Their presence does not verify clean working trees, branches, current compatibility or service health. Read their instructions/source when actually evaluating them. The dated [feasibility research](../../PLAN/knowledge/media/video-editing-feasibility-2026-09-18.md) records upstream observations and limitations; it is not a package lock or runtime test result.

## Decisions that remain open

| Choice | Owner | When needed |
| --- | --- | --- |
| Remote host, visibility and project license | Wixely | Before remote creation/publication; not a blocker for local implementation |
| Broader export formats, delivery presets and snapping tolerance | Agent proposes; Wixely resolves product tradeoffs | After RC-02's bounded matrix; current implementation rejects unaligned cuts |
| Standalone semantic/visual inference provider beyond MCP-supplied observations | Wixely | Optional RC-04 expansion; no implicit cloud disclosure |
| Diarization backend, Qwen TTS model/runtime, voice modes and duration/background handling | Agent evaluates; Wixely resolves material tradeoffs | RC-09 feasibility and dependency adoption |
| Desktop UI and playback backend | Agent, with Wixely on material tradeoffs | RC-06 feasibility gate |
| Image transport/client support, payload limits and scope beyond timed still-image insertion | Agent validates contracts; Wixely resolves broader compositing scope | RC-10 feasibility |
| Representative labelled ad-removal and multi-speaker examples with quality targets | Wixely / Agent | Semantic-quality and RC-09 acceptance |

None of these choices prevents defining contracts and the deterministic local export slice. Proposed defaults in the brief are assumptions, not confirmed user preferences.

## Remaining work

Remaining: interactive image-client acceptance, live diarization/Qwen integration and voice rendering, desktop UI, broader semantic/export/media/platform acceptance and later hosted web/Docker work. Follow the [MVP milestones](mvp.md) and [work queue](work-queue.md). Recommended next action: **Implementation agent: add a cancellable local Qwen provider after runtime approval and evaluate diarization; Wixely: provide representative multi-speaker and replacement examples**.
