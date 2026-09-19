# Desktop review

- Added: 2026-09-19
- Updated: 2026-09-20
- Owner: Implementation agent
- Review: When CupriFace changes, the proxy format changes or Linux acceptance begins

The RC-06 Windows desktop review surface uses CupriFace 0.26.1. It reads the same portable JSON project as the CLI and MCP host. Selecting a transcript or editorial-evidence row resolves that source time through the current timeline revision, asks the shared application layer for the exact rendered PNG, seeks the playback proxy to the resolved timeline time, and displays its source/timeline timing and active crop description.

After opening the window, RoughCut prepares the first exact frame and the bounded validated timeline proxy in the background. The VP9/Opus WebM proxy includes accepted cuts, reordering, crop rendering, inserted images and applied voice replacements. It is keyed by the exact project JSON hash and revision, capped at 128 MiB and stored under the ignored `.roughcut-preview` directory next to the project. CupriFace.Media supplies native VP9/Opus decoding, an audio-clocked player and play, pause, mute, seek and fullscreen controls. Reloading a changed revision prepares a new proxy without blocking the event loop.

The review panel shows speaker labels and overlap/assignment state. Renaming a speaker uses the existing revision-checked speaker edit API. The crop editor shows an uncropped source frame, a proportional crop rectangle, four corner handles and integer X/Y/width/height controls for the selected video clip. Drag the rectangle to move it or a corner to resize it; live values are clamped to source pixels, and release persists one transactional crop edit. Cancel restores the starting rectangle. Apply and Full frame use the same edit API, while the numeric controls remain the precise keyboard-accessible path. Undo and redo persist inverse/forward speaker or crop operations, so project revision semantics remain consistent across desktop, CLI and MCP callers. A crop refreshes the exact rendered frame and invalidates/rebuilds the playback proxy. Reload clears local undo history and refreshes the selected frame from the saved revision.

Run the window from Windows PowerShell:

```powershell
dotnet run --project src/RoughCut.Desktop -- review artifacts/demo/project.json
```

The **RoughCut desktop review** VS Code launch prompts for the same project path. A deterministic headless render is available for testing:

```powershell
dotnet run --project src/RoughCut.Desktop -- snapshot artifacts/demo/project.json artifacts/demo/review.png
```

The output path must not exist. FFmpeg and FFprobe must be available through the same configuration used by the application layer.

Playback preparation inherits the current export gate: one active video/audio source, no more than 32 clips, no more than 60 seconds and the documented frame/sample alignment and asset rules. Unsupported projects retain the exact-frame poster and show the failure instead of silently approximating the timeline. Frame requests are serialized off the event path; a newer selection cancels the older request and only a fully decoded result is committed. Revision-changing commands are also serialized, while proxy preparation is independently cancellable and discarded when its project revision becomes stale. The native player still loads the complete bounded proxy into memory. General timeline trim, split and reorder controls are not present. The transcript is capped at 500 visible rows, the timeline at 200 clips and evidence at 100 proposals.

Windows headless rendering, dummy-device A/V timing and a framework-dependent Windows x64 publish have executed. The two-second published probe decoded 20 frames with 0.0 ms measured drift growth and zero underruns. An interactive window/debugger session, physical audio-device playback and Linux execution have not.

CupriFace and CupriFace.Shell release packages are stored under `vendor/nuget` with hashes and provenance. They are MIT-licensed public release artifacts. RoughCut does not redistribute CupriFace native media codecs in this slice; preview pixels come from the already validated RoughCut FFmpeg path.

Next owner/action: **Implementation agent: add revision-safe clip trim, split and reorder controls.**
