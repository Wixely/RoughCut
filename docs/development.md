# Development and first CLI slice

- Updated: 2026-09-24
- Review: When CLI commands, SDK or media dependencies change
- Owner: Implementation agent

Use .NET SDK 10.0.300 (latest patch roll-forward in that feature band) and standalone FFmpeg/FFprobe. Package versions are pinned exactly and locked: build machines should restore with `--locked-mode`, and `.\scripts\dependency-report.ps1` prints every resolved package with its licence. See [dependencies, licences and assets](dependencies.md). URL acquisition additionally needs a standalone yt-dlp executable. Full current YouTube support also needs a JavaScript runtime: yt-dlp enables Deno by default and finds it on `PATH` or beside `yt-dlp.exe`, so it needs no configuring when Deno is present; without a runtime, acquisition still works with fewer available formats.

### Where RoughCut looks for executables

Executables do not have to be on `PATH`. Every host — CLI, desktop, stdio MCP, speech and diarization — resolves each one the same way, in order: an explicit command argument, then an environment variable (`ROUGHCUT_FFMPEG`, `ROUGHCUT_FFPROBE`, `ROUGHCUT_YTDLP`, `ROUGHCUT_DENO`), then a tools settings file, and otherwise a bare name left to `PATH`. Run `dotnet run --project src/RoughCut.Cli -- tools` to print what resolves, from where, and whether a configured path is actually present.

The settings file is read from `ROUGHCUT_TOOLS` if set, otherwise `roughcut.tools.json` beside the running executable, otherwise `%LOCALAPPDATA%\RoughCut\tools.json`. Every entry is optional and any missing one falls through to `PATH`; a damaged or unreadable file is ignored rather than allowed to stop a host starting. It is machine configuration and never belongs in a project or the repository.

```json
{
  "schemaVersion": 1,
  "ffmpeg": "C:/tools/ffmpeg/bin/ffmpeg.exe",
  "ffprobe": "C:/tools/ffmpeg/bin/ffprobe.exe",
  "ytDlp": "C:/tools/yt-dlp.exe",
  "deno": "C:/tools/deno.exe"
}
```

VS Code launches inherit this automatically, so no launch profile carries a machine path. Local STT uses the separate `RoughCut.Speech.Cli` with Whisper.net 1.9.1 and a pinned base.en model. Local diarization uses the separate `RoughCut.Diarization.Cli` with sherpa-onnx 1.13.8 and external Pyannote/3D-Speaker ONNX models. The stdio host pins `ModelContextProtocol` 1.4.0. The managed product does not embed Python; the accepted optional Qwen provider runs as a separate loopback WSL/Python service described in the [speaker/voice guide](speaker-and-voice.md). NativeAOT CLI publishing additionally requires the Windows native build toolchain. The desktop host calls two operating-system libraries directly for its file picker — `comdlg32.dll` (`GetOpenFileNameW`, `CommDlgExtendedError`) and `user32.dll` (`GetActiveWindow`) on Windows, and the `zenity` or `kdialog` executable on Linux if one is installed. These are platform components, not redistributed assets; where none is available the launcher hides **Browse for a project…** and keeps file drop and a typed path. Executable/model paths and service endpoints are machine configuration, not project JSON.

From the repository root in Windows PowerShell 5.1:

```powershell
dotnet build RoughCut.slnx
dotnet run --project src/RoughCut.Cli -- help
dotnet run --project tests/RoughCut.Tests -- --media --cli src/RoughCut.Cli/bin/Debug/net10.0/roughcut.dll --mcp src/RoughCut.Mcp/bin/Debug/net10.0/roughcut-mcp.dll
.\scripts\verify.ps1 -PublishAot
dotnet format RoughCut.slnx --verify-no-changes --no-restore
```

The executable test harness exits nonzero on any failure. `dotnet test` is not the test command. Without `--media`, it runs the core contract/store checks without FFmpeg. The PowerShell script builds, tests managed CLI/MCP commands, optionally publishes Windows x64 NativeAOT, then exercises the published CLI too. It writes synthetic fixtures and output beneath ignored `artifacts/tests/`; no personal media is required. Builds/restores may access configured NuGet feeds for packages and SDK/runtime packs.

## Try a local clip

Keep a project and its media together, for example `artifacts/demo/project.json` and `artifacts/demo/media/source.mkv`. Commands reject existing frame/project output files rather than overwrite them.

```powershell
dotnet run --project src/RoughCut.Cli -- inspect artifacts/demo/media/source.mkv
dotnet run --project src/RoughCut.Cli -- create artifacts/demo/media/source.mkv artifacts/demo/project.json
dotnet run --project src/RoughCut.Cli -- validate artifacts/demo/project.json
dotnet run --project src/RoughCut.Cli -- map artifacts/demo/project.json
dotnet run --project src/RoughCut.Cli -- frame artifacts/demo/media/source.mkv 0.35 artifacts/demo/frame.png
dotnet run --project src/RoughCut.Cli -- timeline-frame artifacts/demo/project.json 2 1.5 artifacts/demo/timeline-frame.png
dotnet run --project src/RoughCut.Desktop -- review artifacts/demo/project.json
dotnet run --project src/RoughCut.Desktop -- probe-playback artifacts/demo/project.json 2.0
```

`frame` writes a source PNG and prints JSON metadata. `timeline-frame` requires the exact saved revision, resolves a timeline time through cuts/reordering/crops/image holds, writes the rendered PNG and prints its clip/source mapping. Seconds use invariant decimal notation with up to six fractional digits. The API uses integer ticks and rational time bases. A gap, out-of-range request or stale revision fails. MCP equivalents return PNG bytes directly as image content blocks; see the [stdio host guide](mcp.md).

To update safely, use `edit project.json operations.json <previous-revision>` for transactional trim/set-range/split/remove/reorder/crop/image-insert operations. Alternatively copy the JSON to a candidate, edit it and advance `revision` by one, then use `save candidate.json project.json <previous-revision>`. Preserve `projectId`. The timeline array defines clip order; video `in`/`out` values are source-relative ticks. Image clips use `[0, hold-duration)`, `contain` or `cover` fit, and explicit `audio: "silence"`. Active images require explicit encoded export. `map` calculates output intervals without executing edits.

The new `captions`, `preflight` and `export` commands are documented in the [bounded export guide](export.md), including the supported matrix, encoding policy, retimed SRT and validation report. Default export refuses encoding; `--allow-encode` permits it only in an encoding-enabled project mode. Exports use a new destination directory and never overwrite an existing bundle.

Stores reject unknown JSON fields rather than silently erase unsupported features. An omitted optional field loads as its declared default, because deserialization otherwise leaves it null: a hand-written project that simply leaves out `speakers` or `exportMode` opens rather than failing. Validation reports a missing or null collection as an issue instead of throwing, so the component that explains a malformed project can always run. They cap documents at 4 MiB, validate references/ranges, serialize cooperating writers with an adjacent `.lock` file, flush a same-directory temporary file, then rename it over the previous revision. The lock file remains as a synchronization point. Lock contention is an explicit failure; reload/retry. External editors must use the save command to participate in revision checking. Network filesystem and power-loss durability have not been verified.

## Debug and publish

Open the repository folder in VS Code, select **Run and Debug**, choose a launch option and press **F5**. The workspace recommends Microsoft's C# extension (`ms-dotnettools.csharp`), which supplies the `coreclr` debugger; it is not installed automatically by this setup. Every launch builds the solution in Debug configuration first and uses the workspace root as its working directory.

There are 29 launch profiles in seven ordered groups: **1: App**, **2: Checks**, **3: Acquire and transcribe**, **4: Analyse**, **5: Speakers and voice**, **6: Edit and export** and **7: Hosts**. **▶ RoughCut** comes first and is the end-user path: it starts the desktop app with no arguments, so the window opens on its project launcher and prompts for nothing. The two other App launches open a named project directly, one of them with a pre-rendered WebM preview. The remaining groups cover acquisition, caption assessment/import, Whisper model preparation and transcription, analysis save and automatic application, speaker edits, diarization submission and local Sherpa execution, voice planning/synthesis/preview/apply, project creation and edit batches, source and timeline frames, preflight and both export modes, the stdio MCP host and CLI help. `inspect`, `validate`, `map` and `save` have no launch profile because each is a single CLI command; run them from a terminal. The Sherpa launch prompts for both local models and a speaker count; the Qwen synthesis launch prompts for its loopback endpoint. Command launches prompt for paths, prompts, policies, timestamps and revisions as needed. Paths may be absolute or workspace-relative; enter paths with spaces without surrounding quotes. The example defaults require your own local clip at `artifacts/demo/media/source.mkv`; protocol checks generate synthetic fixtures. The sample analysis assumes a 1/1000 time base, `source-1` and at least eight seconds of source. Frame/new-project paths and export/acquisition destinations must not already exist. Revision-changing commands require the current expected revision.

`probe-playback` prepares or reuses the revision-keyed VP9/Opus proxy, selects SDL's dummy audio device and measures native decode advancement, audio drift and underruns for 0.5 through 10 seconds. It is a headless diagnostic, not physical-device acceptance. See the [desktop guide](desktop.md) for the review host and its current limits, the [analysis guide](analysis.md) for the provider-neutral submission format, review/automatic policies and explicit application commands, and the [speaker/voice guide](speaker-and-voice.md) for correction, synthesis, fitting and replacement-rendering commands.

Set breakpoints in the CLI or shared libraries when using a command launch. The media/CLI/MCP checks launch child processes, so use a direct command launch to debug inside those calls. The MCP launch prompts for an isolated workspace root and waits for protocol input on stdio; normally an MCP client launches the host itself. FFmpeg/FFprobe are needed for media operations; executable overrides are inherited from the VS Code environment. Build targets and DLL paths were exercised; an interactive F5/debugger session has not been tested.

```powershell
dotnet publish src/RoughCut.Cli -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish/win-x64
.\artifacts\publish\win-x64\roughcut.exe help
```

The Windows NativeAOT executable is self-contained for managed code. FFmpeg and FFprobe remain external executables. Publish output includes development symbols; they are not intended release assets. No FFmpeg redistribution or license selection has been made. Linux publishing/execution remains unverified.

## Implementation references

The adapter uses documented [FFprobe stream/frame JSON output](https://ffmpeg.org/ffprobe.html), [FFmpeg frame selection](https://ffmpeg.org/ffmpeg-filters.html#select_002c-aselect), and [concat demuxing](https://ffmpeg.org/ffmpeg-formats.html#concat). The strict-copy proof checks complete datastream structure from the [PNG specification](https://www.w3.org/TR/png-3/), alongside timestamp and decoded-output checks. Persistence uses [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) with reflection disabled. References checked 2026-09-19; recheck before dependency/tool changes. Actual execution evidence takes precedence over assumed compatibility.
