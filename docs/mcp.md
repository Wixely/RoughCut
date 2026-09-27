# Stdio MCP host

- Updated: 2026-09-19
- Review: When the MCP SDK, tool contracts, workspace policy or job schema changes
- Owner: Implementation agent

RoughCut includes a local stdio MCP host over the same project, media and export libraries as the CLI. It uses `ModelContextProtocol` 1.4.0 and requires one explicit workspace root. Tool paths are relative to that root; absolute paths, `..`, missing inputs and reparse points are rejected. No MCP server was installed, registered or connected globally.

Run the host directly:

```powershell
dotnet run --project src/RoughCut.Mcp -- --workspace artifacts/mcp-workspace
```

`ROUGHCUT_WORKSPACE` can supply the root. External executables resolve through the shared order described in the [development guide](development.md): environment variables, then a tools settings file, then `PATH`, so yt-dlp, FFmpeg, FFprobe and Deno need not be on `PATH`. Standard output is reserved for MCP messages.

Set `ROUGHCUT_QWEN_ENDPOINT` to an absolute loopback service root to enable live synthesis, for example `http://127.0.0.1:8080/`. `ROUGHCUT_QWEN_API_KEY` is optional and `ROUGHCUT_QWEN_TIMEOUT_SECONDS` defaults to 600. Non-loopback endpoints are rejected so enabling this tool cannot silently disclose text to a remote service.

`ROUGHCUT_STT_MODEL` names the verified base.en model. Left unset it defaults to `%LOCALAPPDATA%\RoughCut\models\ggml-base.en.bin`, so a host that has fetched the model once needs no configuration. `ROUGHCUT_STT_LANGUAGE` defaults to `en`; `ROUGHCUT_STT_CHUNK_SECONDS` defaults to 30 and must be 5–30. Inference stays local.

Transcription **refuses** when no model file is there, naming `roughcut_fetch_speech_model`, rather than starting a 147 MB download inside a transcribe. That tool checks the file against its published size and SHA-256 and reports where it sits, so the download is paid deliberately and once. The explicit speech CLI `model` command does the same from the command line.

Set `ROUGHCUT_DIARIZATION_SEGMENTATION_MODEL` and `ROUGHCUT_DIARIZATION_EMBEDDING_MODEL` to absolute local ONNX model paths to enable Sherpa diarization. `ROUGHCUT_DIARIZATION_SPEAKER_COUNT` defaults to `0` for threshold clustering; set a known count from 1 through 64 when available. `ROUGHCUT_DIARIZATION_THRESHOLD` defaults to `0.5`. The host uses its co-published `roughcut-diarization.dll` worker; `ROUGHCUT_DIARIZATION_WORKER` can override that absolute path. Models and decoded audio stay local. Cancellation kills the worker process tree and does not persist a partial result.

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
| `roughcut_fetch_speech_model` | Fetch and verify the pinned base.en model deliberately, reporting where it sits |
| `roughcut_transcribe_local` | Run bounded local Whisper transcription over overlapping windows and persist timed speech, SRT and model/source/overlap provenance |
| `roughcut_profile_audio` | Measure a range of a source one window at a time: level, peak, low-band level and share, and silent fraction |
| `roughcut_save_analysis` | Validate and persist source-time evidence/observations, then create conservative revision-bound proposals |
| `roughcut_apply_analysis` | Apply auto-approved removals or explicit reviewed proposal IDs against the exact proposal revision |
| `roughcut_save_diarization` | Persist bounded provider turns, stable speaker mappings and inferred/overlap/unknown assignments while preserving corrections |
| `roughcut_diarize_local` | Run configured local sherpa-onnx diarization and persist the result through the same stable assignment boundary |
| `roughcut_edit_speakers` | Add, rename, assign or merge speaker labels with persisted correction history |
| `roughcut_plan_voice_replacement` | Record a Qwen voice mapping and bounded replacement request |
| `roughcut_import_voice_preview` | Import bounded PCM WAVE with synthesis provenance |
| `roughcut_synthesize_voice` | Validate the configured loopback Qwen service and generate a revision-safe bounded preview |
| `roughcut_get_voice_preview` | Return metadata and an actual `audio/wav` MCP content block |
| `roughcut_set_voice_replacement_state` | Apply or revert an exact or bounded time-stretch preview; applied audio renders through encoding-authorized export |
| `roughcut_import_image` | Validate an incoming base64 PNG and add a content-addressed image asset with provenance |
| `roughcut_create_project_from_url` | Acquire a URL with subtitles, create the project and select captions in one step |
| `roughcut_acquire_url` | Run configured standalone yt-dlp for one bounded staged media/subtitle acquisition |
| `roughcut_list_source_formats` | List a URL's renditions with size, bitrate and codecs so the caller picks one or a bitrate policy |
| `roughcut_preflight_export` | Return exact supported/unsupported export decisions |
| `roughcut_start_export` | Queue a persisted export job |
| `roughcut_preflight_delivery` | Report whether the timeline can be re-encoded to one H.264/AAC MP4, without starting the media tools |
| `roughcut_start_delivery` | Queue a persisted job that re-encodes the timeline into a delivery file |
| `roughcut_list_cut_points` | Report the copy anchors around a time, their offsets, audio alignment and the source's median keyframe interval |
| `roughcut_preflight_mux` | Resolve every boundary onto a copy anchor and report the plan, without encoding anything |
| `roughcut_start_mux` | Queue a persisted job that copies the retained material into a new container |
| `roughcut_get_job` | Read job state and coarse progress |
| `roughcut_cancel_job` | Request queued/running job cancellation |

Incoming PNGs are capped at 8 MiB and 8K pixels, require valid chunk checksums, and currently support non-interlaced 8-bit RGB/RGBA. Assets are stored below the project as `assets/images/<sha256>.png`; a successful import advances the expected project revision. Use `roughcut_apply_edits` with `insert-image`, a new clip ID, asset ID, duration, optional `beforeClipId`, and `contain` or `cover` fit. Timeline preview requires the expected revision and returns the fitted/cropped pixels used by export. Active image clips require explicit encoded export and carry silence; strict copy-only mode rejects them.

Delivery is the second export path, described in the [export guide](export.md). It always re-encodes and never claims a copy, so it renders ordinary acquired media the strict matrix refuses. Its preflight starts no process, and its job record carries `mode: "delivery"`; checkpoints written before delivery existed read back as `strict`.

Mux is the third ([decision 0022](decisions/0022-stream-copy-mux-export.md)), and the fast one: it copies packets and decodes nothing, so each segment's start snaps to a source keyframe while its end stays exact. `roughcut_list_cut_points` exists so a caller can see the anchors around the time it cares about — with their offsets and whether audio aligns there — and then decide between copying and delivery's exact re-encode. `roughcut_preflight_mux` reads the media, unlike the delivery preflight, because anchors are a property of the file.

Measurement tools return numbers, never verdicts ([decision 0023](decisions/0023-measurement-inside-roughcut.md)). `roughcut_profile_audio` reports what the sound does window by window, including the share of energy below a band split, which separates material carrying bass and drums from speech at the same loudness. Where the sections are, and what to cut, stays the caller's decision.

Jobs are serialized atomically under `<workspace>/.roughcut/jobs`, retain at most 100 records and run exports one at a time. An interrupted `queued` or `running` checkpoint becomes `failed` when the next host starts; automatic resume is not claimed. Export staging cleanup and atomic bundle publication remain the export engine's responsibility.

The executable protocol checks launch the host with the official MCP client, list all 34 tools, persist stable diarization assignments, verify safe missing-configuration behavior for local Sherpa and Whisper, synthesize through a loopback HTTP fixture, receive and decode source/timeline PNG and voice-preview WAVE content blocks, exercise reversible speaker/voice changes, assess captions, persist evidence-backed analysis, prove uncertain removals remain review-only, reject workspace escapes and stale revisions, import/insert a PNG, complete an encoded image export, complete a delivery job that publishes an H.264/AAC MP4, and cancel a separate durable export without publishing output. Live Sherpa and WSL/CUDA Qwen provider acceptance are recorded separately. The stdio host is a normal .NET deployment because its reflection-based MCP schema and native speech runtimes have not been qualified for NativeAOT.

Analysis submissions are untrusted data. MCP callers supply provider/model identity, bounded source-time evidence and observations; RoughCut validates and maps them through the policy described in the [analysis guide](analysis.md). No MCP tool executes provider-generated commands or silently discloses media to another service.
