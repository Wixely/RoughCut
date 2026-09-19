# Desktop review

- Added: 2026-09-19
- Owner: Implementation agent
- Review: When CupriFace changes or continuous playback is added

The first RC-06 slice is a Windows desktop review surface built with CupriFace 0.26.1. It reads the same portable JSON project as the CLI and MCP host. Selecting a transcript or editorial-evidence row resolves that source time through the current timeline revision, asks the shared application layer for the exact rendered PNG, and displays its source/timeline timing and active crop description.

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

The desktop currently decodes a still PNG after selection. It does not yet provide continuous video/audio playback, audio synchronization, scrubbing, an editable crop overlay or general timeline edits. Frame decoding runs synchronously inside the UI click handler, so a long source can pause the window until the bounded FFmpeg operation completes. The transcript is capped at 500 visible rows, the timeline at 200 clips and evidence at 100 proposals. Windows headless rendering and a framework-dependent Windows x64 publish have executed; an interactive window/debugger session and Linux execution have not.

CupriFace and CupriFace.Shell release packages are stored under `vendor/nuget` with hashes and provenance. They are MIT-licensed public release artifacts. RoughCut does not redistribute CupriFace native media codecs in this slice; preview pixels come from the already validated RoughCut FFmpeg path.

Next owner/action: **Implementation agent: add seekable synchronized A/V playback and move frame work off the UI event path, then add crop interaction.**
