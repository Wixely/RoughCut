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
| `roughcut_import_image` | Validate an incoming base64 PNG and add a content-addressed image asset with provenance |
| `roughcut_preflight_export` | Return exact supported/unsupported export decisions |
| `roughcut_start_export` | Queue a persisted export job |
| `roughcut_get_job` | Read job state and coarse progress |
| `roughcut_cancel_job` | Request queued/running job cancellation |

Incoming PNGs are capped at 8 MiB and 8K pixels, require valid chunk checksums, and currently support non-interlaced 8-bit RGB/RGBA. Assets are stored below the project as `assets/images/<sha256>.png`; a successful import advances the expected project revision. Use `roughcut_apply_edits` with `insert-image`, a new clip ID, asset ID, duration, optional `beforeClipId`, and `contain` or `cover` fit. Timeline preview requires the expected revision and returns the fitted/cropped pixels used by export. Active image clips require explicit encoded export and carry silence; strict copy-only mode rejects them.

Jobs are serialized atomically under `<workspace>/.roughcut/jobs`, retain at most 100 records and run exports one at a time. An interrupted `queued` or `running` checkpoint becomes `failed` when the next host starts; automatic resume is not claimed. Export staging cleanup and atomic bundle publication remain the export engine's responsibility.

The executable protocol checks launch the host with the official MCP client, list all 12 tools, receive and decode source and revision-aware timeline PNG content blocks, reject workspace escapes and stale revisions, import and insert a PNG, complete and validate the encoded image export, and cancel a separate durable export without publishing output. The stdio host is currently a normal .NET deployment; NativeAOT publication has not been evaluated for the reflection-based MCP tool schema.
