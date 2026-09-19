# Next-agent handoff

- Updated: 2026-09-19
- Review: At the start of implementation and before dependency adoption
- Owner: Implementation agent

## Current state

The [working MVP](mvp.md) now has shared .NET 10 core/media/application APIs, CLI commands and a [stdio MCP host](mcp.md) for projects, revision-safe edits, bounded live acquisition/caption selection, local Whisper transcription, supplied SRT/PNG import, source/timeline frame retrieval, timed image insertion and durable [bounded validated export](export.md) jobs. Windows managed verification passes 37 checks; see [RC-03 live evidence](evidence/2026-09-19-rc03-live.md), [timed-image evidence](evidence/2026-09-19-timed-images.md), [export evidence](evidence/2026-09-19-export.md) and [commands](development.md). There is no GUI, semantic inference, diarization or Qwen runtime yet. No remote or project license is selected.

The [implementation brief](product-and-architecture.md) carries forward the complete discovery plan, including requirements, architecture boundaries, illustrative JSON, export semantics, reuse candidates, open questions and risks. The original [PLAN note](../../PLAN/inbox/roughcut.md) is historical discovery context; maintain new implementation decisions here.

## Continue with evidence-backed analysis (RC-04)

On 2026-09-19 the user added speaker distinction and voice replacement via Qwen TTS. These are confirmed product requirements, now covered by the implementation brief and RC-09. No diarization backend, Qwen model/runtime or deployment mode has been adopted or tested. Include speaker assignments, corrections, voice mappings and replacement provenance in RC-01; keep the initial deterministic export proof bounded. RC-09 follows the speech/export foundations and joins CLI/MCP/desktop before release validation. Strict copy-only export must reject active voice replacements.

The user also required images retrieved from any valid video timestamp for agents to inspect through MCP, and incoming images through MCP for agent-generated content. The official MCP client now receives/decodes actual PNG content blocks with precise timing, imports bounded checksummed PNG assets with provenance, inserts them transactionally, previews the exact saved revision and completes validated encoded export. Interactive AI-agent visual interpretation has not been tested.

The core now includes caption provenance/retiming, transactional edit batches and export plan/report contracts. The exporter proves copy-only PNG/PCM Matroska cuts, rejects unsupported strict requests, encodes crop/H.264 input when explicitly permitted, and validates every decoded frame, PCM sample, presentation timestamp and copied packet payload before publishing an atomic bundle. It is deliberately limited to the matrix in the export guide. Do not expand safe-copy claims from this one matrix.

The bounded yt-dlp adapter, caption assessment/override and timed local-STT processor are implemented. Live Windows acceptance used standalone yt-dlp 2026.07.04 with pinned Deno 2.9.7 and downloaded a public 19-second YouTube source plus manual English captions. Whisper.net/runtime 1.9.1 transcribed a synthetic 77.1-second source in three 30-second-or-shorter chunks and persisted timed SRT and model/source provenance. Bantz was left unchanged because its public result discards segment times. Continue with conservative analysis proposals that retain uncertain material; structured evidence, speaker correction history and synthesis provenance remain contract work.

Extend the synthetic fixtures and executable test harness without personal media or downloads. Keep an explicit allow/reject policy so it will work through MCP without an interactive dialog. Build/run instructions and VS Code configurations now exist; keep them aligned with changes.

VS Code has 20 launch options covering the stdio MCP host, current CLI commands including acquisition/caption assessment/timeline preview, Whisper model preparation/transcription, separate exports with/without encoding, and contract/protocol checks. All use the explicit Debug build task. See the development guide for F5 usage, extension requirements and input defaults. Interactive debugger behavior remains unverified.

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
| Semantic/visual inference mode and provider | Wixely | RC-04; no implicit cloud disclosure |
| Diarization backend, Qwen TTS model/runtime, voice modes and duration/background handling | Agent evaluates; Wixely resolves material tradeoffs | RC-09 feasibility and dependency adoption |
| Desktop UI and playback backend | Agent, with Wixely on material tradeoffs | RC-06 feasibility gate |
| Image transport/client support, payload limits and scope beyond timed still-image insertion | Agent validates contracts; Wixely resolves broader compositing scope | RC-10 feasibility |
| Representative labelled ad-removal example and quality targets | Wixely / Agent | RC-04 acceptance |

None of these choices prevents defining contracts and the deterministic local export slice. Proposed defaults in the brief are assumptions, not confirmed user preferences.

## Remaining work

Remaining: evidence-backed analysis, interactive image-client acceptance, richer speech contracts, speaker/Qwen integration, desktop UI, broader export/media/platform acceptance and later hosted web/Docker work. Follow the [MVP milestones](mvp.md) and [work queue](work-queue.md). Recommended next action: **Implementation agent: implement RC-04 analysis proposals; Wixely: provide a representative labelled ad-removal example**.
