# Next-agent handoff

- Updated: 2026-09-19
- Review: At the start of implementation and before dependency adoption
- Owner: Implementation agent

## Current state

The [working MVP](mvp.md) now has shared .NET 10 core/media/application APIs, CLI commands, a [stdio MCP host](mcp.md) and a [CupriFace desktop review host](desktop.md) for projects, revision-safe edits, bounded live acquisition/caption selection, local Whisper transcription and process-isolated Sherpa diarization, provider-neutral [evidence-backed analysis](analysis.md), stable [speaker corrections, loopback Qwen previews and fitted replacement rendering](speaker-and-voice.md), supplied SRT/PNG import, source/timeline frame retrieval, timed image insertion and durable [bounded validated export](export.md) jobs. Windows managed and NativeAOT-CLI verification each pass 54 checks. Windows Sherpa execution/cancellation, live WSL/CUDA Qwen synthesis/rendering, framework-dependent desktop snapshot/crop execution and published synchronized proxy playback have passed for their bounded configurations. The desktop shows exact frames, persisted speaker and numeric crop edits, a proportional source crop overlay and a revision-keyed VP9/Opus rendering of the validated timeline. Its frame and proxy preparation run in the background with cancellation, stale-result checks and serialized revision writes. There is no standalone semantic provider. No remote or project license is selected.

The [implementation brief](product-and-architecture.md) carries forward the complete discovery plan, including requirements, architecture boundaries, illustrative JSON, export semantics, reuse candidates, open questions and risks. The original [PLAN note](../../PLAN/inbox/roughcut.md) is historical discovery context; maintain new implementation decisions here.

## Continue desktop review (RC-06)

On 2026-09-19 the user added speaker distinction and voice replacement via Qwen TTS. The bounded RC-09 workflow runs Sherpa locally, persists stable cluster-to-project mappings and source/model/submission provenance, preserves reviewed assignments and redirects mappings after merges; CLI/MCP can synthesize or import, preview, apply/revert, preflight and render exact or bounded `time-stretch` replacements. Whisper remains responsible for transcript text and timing. A Windows x64 Sherpa CLI separated the official two-speaker fixture; native inference now runs in a child process so cancellation kills the process tree without changing the saved project. A WSL2/CUDA 1.7B CustomVoice preview has been rendered into exact 48 kHz output. Strict copy-only rejects replacements. Rendering requires a corrected, non-overlapping interval fully retained exactly once and the reviewed `require-isolated-dialogue` assertion. Source separation, background preservation inside the interval, fades and caption-text rewrite do not exist.

The user also required images retrieved from any valid video timestamp for agents to inspect through MCP, and incoming images through MCP for agent-generated content. The official MCP client now receives/decodes actual PNG content blocks with precise timing, imports bounded checksummed PNG assets with provenance, inserts them transactionally, previews the exact saved revision and completes validated encoded export. Interactive AI-agent visual interpretation has not been tested.

The core now includes caption provenance/retiming, transactional edit batches and export plan/report contracts. The exporter proves copy-only PNG/PCM Matroska cuts, rejects unsupported strict requests, encodes crop/H.264 input when explicitly permitted, and validates every decoded frame, PCM sample, presentation timestamp and copied packet payload before publishing an atomic bundle. It is deliberately limited to the matrix in the export guide. Do not expand safe-copy claims from this one matrix.

The bounded RC-04 workflow accepts source-time evidence/observations through `IContentAnalyzer`, CLI or MCP, records provider/model/prompt/source provenance, maps observations to clips and persists revision-bound proposals. `review` never auto-removes; `auto-high-certainty` removes only a high-certainty provider recommendation. Lower certainty remains review-only. Automatic and explicit reviewed application preserve surrounding material and clear stale proposals. The tests use labelled synthetic ad/no-ad/uncertain observations; no standalone model or real-media quality claim is made.

The bounded yt-dlp adapter, caption assessment/override and timed local-STT processor remain verified. Live Windows acceptance used standalone yt-dlp 2026.07.04 with pinned Deno 2.9.7. Whisper.net/runtime 1.9.1 transcribed a 77.1-second synthetic source in three chunks. Bantz remains unchanged because its public result discards segment times.

Extend the synthetic fixtures and executable test harness without personal media or downloads. Keep an explicit allow/reject policy so it will work through MCP without an interactive dialog. Build/run instructions and VS Code configurations now exist; keep them aligned with changes.

VS Code has 31 launch options covering the desktop review host, stdio MCP host, current CLI commands including analysis save/automatic apply, diarization submission/local execution, speaker and voice synthesis/preview operations, acquisition/caption assessment/timeline preview, Whisper model preparation/transcription, separate exports with/without encoding, and contract/protocol checks. All use the explicit Debug build task. See the development guide for F5 usage, extension requirements and input defaults. Interactive debugger behavior remains unverified.

## Local environment observations

Read-only checks on 2026-09-18 found .NET SDK 10.0.300 (also 8.0.419), Git 2.53.0.windows.2, and FFmpeg/FFprobe executables resolvable on PATH. Their versions, codecs and execution behavior were not validated. `yt-dlp` was not found on PATH; it may exist elsewhere. No tools, runtimes, models or packages were installed. Recheck at use time; absence from PATH is not proof of absence from the machine.

On 2026-09-19, SDK 10.0.300 and FFmpeg/FFprobe `2026-04-01-git-eedf8f0165-full_build` were executed. Windows x64 NativeAOT publishing and the CLI command checks passed. The main CLI stays independent of native Whisper and remains the NativeAOT target; speech CLI and MCP are normal .NET deployments. The current media adapter intentionally scans from the beginning with bounded output/time/frame count; it is not ready for arbitrary long videos. See the evidence for exact fixture coverage and rejected display formats.

The same toolchain passed the export regression suite. The fixture uses font-free visible frame IDs because font-based drawtext crashed this FFmpeg build. Copy selection scans from the beginning to avoid keyframe-seek audio preroll; do not reintroduce input-side seeking without the H.264/audio-copy regression. No Linux, disk-full or crash-durability result is claimed.

Sibling Bantz, DNAX, MCPSharp and MCPHub directories were observed but not adopted merely from their presence. CupriFace source was inspected for RC-06 without modification; its local MediaProbe lacked codec artifacts, then the official 0.26.1 Media package supplied the release-native assets and passed the bounded published playback probe recorded in the [desktop playback evidence](evidence/2026-09-19-desktop-playback.md). Recheck instructions, source and working trees before further sibling-library work. The dated [feasibility research](../../PLAN/knowledge/media/video-editing-feasibility-2026-09-18.md) records upstream observations and limitations; it is not a package lock or runtime test result.

## Decisions that remain open

| Choice | Owner | When needed |
| --- | --- | --- |
| Remote host, visibility and project license | Wixely | Before remote creation/publication; not a blocker for local implementation |
| Broader export formats, delivery presets and snapping tolerance | Agent proposes; Wixely resolves product tradeoffs | After RC-02's bounded matrix; current implementation rejects unaligned cuts |
| Standalone semantic/visual inference provider beyond MCP-supplied observations | Wixely | Optional RC-04 expansion; no implicit cloud disclosure |
| representative diarization quality and background-aware replacement handling | Agent evaluates; Wixely supplies representative material and resolves tradeoffs | Remaining RC-09 implementation |
| Direct crop dragging/general editing and physical-device playback | Agent, with Wixely on material tradeoffs | Remaining RC-06 feasibility gate |
| Image transport/client support, payload limits and scope beyond timed still-image insertion | Agent validates contracts; Wixely resolves broader compositing scope | RC-10 feasibility |
| Representative labelled ad-removal and multi-speaker examples with quality targets | Wixely / Agent | Semantic-quality and RC-09 acceptance |

None of these choices prevents defining contracts and the deterministic local export slice. Proposed defaults in the brief are assumptions, not confirmed user preferences.

## Remaining work

Remaining: direct crop dragging/general editing, physical-device/window and Linux playback acceptance, representative diarization quality, interactive image-client acceptance, background-aware voice work, broader semantic/export/media/platform acceptance and later hosted web/Docker work. Follow the [MVP milestones](mvp.md) and [work queue](work-queue.md). Recommended next action: **Implementation agent: add direct crop dragging and measure speaker/overlap error when fixtures are available; Wixely: provide representative multi-speaker and replacement examples**.
