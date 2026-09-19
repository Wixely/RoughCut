# Desktop review

- Added: 2026-09-19
- Owner: Implementation agent
- Review: When CupriFace changes, the proxy format changes or Linux acceptance begins

The RC-06 Windows desktop review surface uses CupriFace 0.26.1. It reads the same portable JSON project as the CLI and MCP host. Selecting a transcript or editorial-evidence row resolves that source time through the current timeline revision, asks the shared application layer for the exact rendered PNG, seeks the playback proxy to the resolved timeline time, and displays its source/timeline timing and active crop description.

Before opening the window, RoughCut runs the bounded validated timeline export and transcodes that result into a VP9/Opus WebM proxy. The proxy includes accepted cuts, reordering, crop rendering, inserted images and applied voice replacements. It is keyed by the exact project JSON hash and revision, capped at 128 MiB and stored under the ignored `.roughcut-preview` directory next to the project. CupriFace.Media supplies native VP9/Opus decoding, an audio-clocked player and play, pause, mute, seek and fullscreen controls. Reloading a changed revision prepares a new proxy.

The review panel shows speaker labels and overlap/assignment state. Renaming a speaker uses the existing revision-checked speaker edit API. Undo and redo are new persisted inverse/forward renames, so correction history and revision semantics remain consistent across desktop, CLI and MCP callers. Reload clears local undo history and refreshes the selected frame from the saved revision.

Run the window from Windows PowerShell:

```powershell
dotnet run --project src/RoughCut.Desktop -- review artifacts/demo/project.json
```

The **RoughCut desktop review** VS Code launch prompts for the same project path. A deterministic headless render is available for testing:

```powershell
dotnet run --project src/RoughCut.Desktop -- snapshot artifacts/demo/project.json artifacts/demo/review.png
```

The output path must not exist. FFmpeg and FFprobe must be available through the same configuration used by the application layer.

Playback preparation inherits the current export gate: one active video/audio source, no more than 32 clips, no more than 60 seconds and the documented frame/sample alignment and asset rules. Unsupported projects retain the exact-frame poster and show the failure instead of silently approximating the timeline. Initial proxy preparation happens before the window opens, reload preparation and exact-frame selection currently block the UI path, and the native player loads the complete bounded proxy into memory. There is no editable crop overlay or general timeline editing yet. The transcript is capped at 500 visible rows, the timeline at 200 clips and evidence at 100 proposals.

Windows headless rendering, dummy-device A/V timing and a framework-dependent Windows x64 publish have executed. The two-second published probe decoded 20 frames with 0.0 ms measured drift growth and zero underruns. An interactive window/debugger session, physical audio-device playback and Linux execution have not.

CupriFace and CupriFace.Shell release packages are stored under `vendor/nuget` with hashes and provenance. They are MIT-licensed public release artifacts. RoughCut does not redistribute CupriFace native media codecs in this slice; preview pixels come from the already validated RoughCut FFmpeg path.

Next owner/action: **Implementation agent: add crop interaction and move proxy/frame preparation off the UI event path.**
