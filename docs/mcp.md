# Stdio MCP host

- Updated: 2026-09-19
- Review: When the MCP SDK, tool contracts, workspace policy or job schema changes
- Owner: Implementation agent

RoughCut includes a local stdio MCP host over the same project, media and export libraries as the CLI. It uses `ModelContextProtocol` 1.4.0 and requires one explicit workspace root. Tool paths are relative to that root; absolute paths, `..`, missing inputs and reparse points are rejected. No MCP server was installed, registered or connected globally.

Run the host directly:

```powershell
dotnet run --project src/RoughCut.Mcp -- --workspace artifacts/mcp-workspace
```

`ROUGHCUT_WORKSPACE` can supply the root. `ROUGHCUT_FFMPEG` and `ROUGHCUT_FFPROBE` override the external media executables. Standard output is reserved for MCP messages.

Set `ROUGHCUT_QWEN_ENDPOINT` to an absolute loopback service root to enable live synthesis, for example `http://127.0.0.1:8080/`. `ROUGHCUT_QWEN_API_KEY` is optional and `ROUGHCUT_QWEN_TIMEOUT_SECONDS` defaults to 600. Non-loopback endpoints are rejected so enabling this tool cannot silently disclose text to a remote service.

Set `ROUGHCUT_STT_MODEL` to the verified base.en model path to enable local transcription. `ROUGHCUT_STT_LANGUAGE` defaults to `en`; `ROUGHCUT_STT_CHUNK_SECONDS` defaults to 30 and must be 5–30. Inference stays local, while the explicit speech CLI `model` command can download and verify the model beforehand.

The current tools are:

| Tool | Behavior |
| --- | --- |
| `roughcut_read_project` | Validate and return a project and current revision |
| `roughcut_inspect_video` | Inspect bounded local video metadata |
| `roughcut_create_project` | Create a portable project for workspace media |
| `roughcut_get_frame` | Return timing JSON and an actual `image/png` MCP content block |
| `roughcut_get_timeline_frame` | Resolve an exact project revision/timeline time and return metadata plus the rendered `image/png` content block |
| `roughcut_apply_edits` | Apply one revision-checked edit batch |
| `roughcut_import_captions` | Import a bounded project-local SRT |
| `roughcut_select_captions` | Assess SRT candidates by provenance, language and coverage, then persist a recommendation or explicit override |
| `roughcut_transcribe_local` | Run bounded local Whisper transcription and persist timed speech, SRT and model/source provenance |
| `roughcut_save_analysis` | Validate and persist source-time evidence/observations, then create conservative revision-bound proposals |
| `roughcut_apply_analysis` | Apply auto-approved removals or explicit reviewed proposal IDs against the exact proposal revision |
| `roughcut_edit_speakers` | Add, rename, assign or merge speaker labels with persisted correction history |
| `roughcut_plan_voice_replacement` | Record a Qwen voice mapping and bounded replacement request |
| `roughcut_import_voice_preview` | Import bounded PCM WAVE with synthesis provenance |
| `roughcut_synthesize_voice` | Validate the configured loopback Qwen service and generate a revision-safe bounded preview |
| `roughcut_get_voice_preview` | Return metadata and an actual `audio/wav` MCP content block |
| `roughcut_set_voice_replacement_state` | Apply or revert an exact-duration preview |
| `roughcut_import_image` | Validate an incoming base64 PNG and add a content-addressed image asset with provenance |
| `roughcut_acquire_url` | Run configured standalone yt-dlp for one bounded staged media/subtitle acquisition |
| `roughcut_preflight_export` | Return exact supported/unsupported export decisions |
| `roughcut_start_export` | Queue a persisted export job |
| `roughcut_get_job` | Read job state and coarse progress |
| `roughcut_cancel_job` | Request queued/running job cancellation |

Incoming PNGs are capped at 8 MiB and 8K pixels, require valid chunk checksums, and currently support non-interlaced 8-bit RGB/RGBA. Assets are stored below the project as `assets/images/<sha256>.png`; a successful import advances the expected project revision. Use `roughcut_apply_edits` with `insert-image`, a new clip ID, asset ID, duration, optional `beforeClipId`, and `contain` or `cover` fit. Timeline preview requires the expected revision and returns the fitted/cropped pixels used by export. Active image clips require explicit encoded export and carry silence; strict copy-only mode rejects them.

Jobs are serialized atomically under `<workspace>/.roughcut/jobs`, retain at most 100 records and run exports one at a time. An interrupted `queued` or `running` checkpoint becomes `failed` when the next host starts; automatic resume is not claimed. Export staging cleanup and atomic bundle publication remain the export engine's responsibility.

The executable protocol checks launch the host with the official MCP client, list all 23 tools, synthesize through a loopback HTTP fixture, receive and decode source/timeline PNG and voice-preview WAVE content blocks, exercise reversible speaker/voice changes, assess captions, persist evidence-backed analysis, prove uncertain removals remain review-only, reject workspace escapes and stale revisions, verify actionable missing-model behavior for local STT, import/insert a PNG, complete an encoded image export, and cancel a separate durable export without publishing output. Live WSL/CUDA provider acceptance is recorded separately. The stdio host is a normal .NET deployment because both its reflection-based MCP schema and native Whisper loading have not been qualified for NativeAOT.

Analysis submissions are untrusted data. MCP callers supply provider/model identity, bounded source-time evidence and observations; RoughCut validates and maps them through the policy described in the [analysis guide](analysis.md). No MCP tool executes provider-generated commands or silently discloses media to another service.
