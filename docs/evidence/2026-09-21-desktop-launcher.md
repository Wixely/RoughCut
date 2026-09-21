# Desktop launcher and supplied-preview evidence

- Date: 2026-09-21
- Platform: Windows 10 Enterprise, .NET SDK 10.0.300, FFmpeg/FFprobe `N-124591-g5b38e6eafb-20260522`
- Scope: RC-06 end-user project launcher, recent-project history, and supplied review proxies surviving edits

## Project launcher

`roughcut-desktop` with no arguments now opens the window on a project launcher instead of printing help and exiting. The launcher lists the projects opened most recently on this computer, offers **New project from a video…** and **Open a project…**, accepts a dropped file, and takes a typed path. `review <project.json>` still opens a project directly, and **Open another** returns to the launcher.

## Creating projects from a URL

Pasting a URL and pressing **Fetch** runs `RoughCutOperations.CreateProjectFromUrlAsync`: acquire the video with its subtitles, create `project.json` beside the downloaded media, then assess and select the caption track with provenance. The compound operation lives in the application layer rather than the window, because every product workflow has to be possible headlessly; the CLI `create-url` command and the `roughcut_create_project_from_url` MCP tool call the same code, so a host cannot drift from the yt-dlp policy or compose the three steps differently.

That policy is the point: `--write-subs --write-auto-subs --sub-langs en,-live_chat --sub-format srt/best --convert-subs srt`, with `--ignore-config`, `--no-playlist`, `--no-js-runtimes` unless Deno is configured, and the existing size bounds. The check asserts those arguments are actually passed, so an agent never has to know them and a future edit cannot quietly drop subtitles.

Downloads land in a dated folder under `%USERPROFILE%\Videos\RoughCut`, overridable with `ROUGHCUT_PROJECTS`. The desktop now reads `ROUGHCUT_YTDLP`, `ROUGHCUT_DENO`, `ROUGHCUT_FFMPEG` and `ROUGHCUT_FFPROBE`; it previously ignored all four, so acquisition would only have worked with yt-dlp already on `PATH`.

## Creating projects from a local video

A new project is created beside its video and named after it, through the same `RoughCutOperations.CreateProjectAsync` the CLI `create` command uses. Location is not a free choice: an asset's stored path must be relative and inside the project directory to stay portable, which the validator enforces, so the video's own folder is the only always-valid home. An existing file is never overwritten — the next free `name-2.json`, `name-3.json` is taken. The drop target and the path field route by extension, `.json` to open and anything else to create, so creation still works where no native picker exists.

## File picker

CupriFace 0.26.1 exposes no operating-system file dialog — its `DialogComponent` is an in-page dialog, and its only file entry point is `CupriDocument.OnFileDrop` with `DroppedFile.Path`. Its shell ships SDL2, which predates SDL3's `SDL_ShowOpenFileDialog`, and windowing is GLFW, which has no dialog either. The picker therefore calls the platform: `GetOpenFileNameW` in `comdlg32.dll` on Windows, and `zenity` or `kdialog` on Linux. `user32!GetActiveWindow` supplies the owner window, read on the UI thread so the dialog belongs to the RoughCut window; `SkiaWindow.Win32Hwnd` exists but `DesktopHost.Run` never hands the window to the application. The dialog runs on its own STA thread — the Windows common dialog loads shell extensions that require one — so the render loop keeps running while it is open.

`OFN_NOCHANGEDIR` is documented as ineffective for `GetOpenFileName`, and the first probe run proved it: browsing moved the process working directory to the dialog's folder and nine later MCP checks failed with "Could not execute because the specified command or file was not found" because their relative paths no longer resolved. The picker now saves and restores the working directory around the dialog, and the probe asserts it. Callers still see a window during which the process working directory is the dialog's; RoughCut itself resolves project paths to absolute form before use.

History is per-user window state at `%LOCALAPPDATA%\RoughCut\recent-projects.json`, deliberately outside project JSON and outside the repository: at most ten entries, most recent first, deduplicated by path, with missing files dropped on read and any damage ignored. `ROUGHCUT_RECENT_PROJECTS` overrides the location so tests and headless renders are deterministic.

## Supplied review proxies

A WebM supplied with `review <project.json> <preview.webm>` is now the session's playback for as long as the session lasts. Before this change every edit, undo, redo and reload cleared it and started a validated proxy build — which fails for exactly the sources a preview is supplied for, so the first edit left the reviewer with no playback and an error. The supplied render is now restored instead, and the status line reports that it does not show edits since the revision it was supplied at.

## Executed

```powershell
dotnet build RoughCut.slnx
dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli src\RoughCut.Cli\bin\Debug\net10.0\roughcut.dll --desktop src\RoughCut.Desktop\bin\Debug\net10.0\roughcut-desktop.dll --mcp src\RoughCut.Mcp\bin\Debug\net10.0\roughcut-mcp.dll
dotnet run --project src\RoughCut.Desktop --no-build -- snapshot artifacts\debug-launcher.png
dotnet run --project src\RoughCut.Desktop --no-build -- help
```

Observed: managed verification passed 63 checks with no failures, including six new ones. The URL check drives the whole sequence through an injected acquisition tool that supplies a real fixture video, so it runs without a network: it asserts the subtitle and safety arguments were passed, that the project landed inside the acquired directory with redacted provenance and a portable media path, that the English manual track was selected and recorded at revision 2, and that a subtitle-free source still produces a valid revision-1 project with the reason reported rather than an error. The creation check builds a project from a copied fixture video and asserts it lands beside the video under the video's name, starts at revision 1 with one asset and one clip covering the whole source, stores a portable relative asset path, validates, and that a second creation for the same video takes `-2` rather than overwriting the first; a missing file and a non-video file are both rejected. The picker check is not a mock: it calls `NativeFileDialog.OpenFile` on a background thread, waits for a real window of the Windows dialog class `#32770` carrying the requested title, posts `WM_CLOSE` to it, and asserts the call reported cancellation and left the process working directory unchanged. It skips itself when the platform is not Windows or the session is not interactive. The supplied-proxy check confirms the same proxy instance survives a reorder, an explicit playback preparation, an undo and a reload, that the status gains its behind-the-timeline note exactly when the revision moves, and that a rejected replacement (empty file, wrong extension, missing file) leaves the accepted one in place. The history check covers ordering, the ten-entry bound, reopen deduplication, pruning of deleted files, forgetting, tolerance of a corrupt file and recovery afterwards. The launcher check asserts that with no project the launcher is shown, the workspace hidden and the recorded project rendered with its file name, folder and project ID, and that constructing with a session shows the workspace instead.

A 1280×800 headless render of the launcher with two seeded history entries confirmed the final layout. Two earlier layouts were rejected from their renders: repeating a multi-child row made CupriFace overlap the rows and the path field, and `width:100%` without `box-sizing:border-box` pushed rows past the card edge.

## Limitations

**No live URL was fetched.** The URL workflow is proven against an injected acquisition tool, not against yt-dlp or a real host; the last live acquisition evidence remains the 2026-09-20 long-form run. A download shows no progress and cannot be cancelled from the window, and the dated destination folder is unique but not descriptive.

No interactive window was run: the launcher is verified by headless render, model assertions and the shared document path, not by a live pointer or a real drag-and-drop. Opening a project by clicking a recent entry is covered at the model and rendering level; the click itself is not synthesized. The Windows dialog is proven to open and cancel, but choosing a file in it and the path from that selection into an opened project were not driven automatically. The Linux `zenity`/`kdialog` path is written but has never been executed — no Linux desktop run exists for this project at all — and it fails closed to the typed path. Per-entry **Forget** is not in the UI — entries leave the list when their file disappears, and the API exists for a later control. The Windows x64 NativeAOT CLI path remains unbuildable on this machine (no MSVC platform linker), and the FFmpeg first found on `PATH` is a 2018 build that fails every media check, so a separately installed current build was used.
