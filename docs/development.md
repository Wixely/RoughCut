# Development and first CLI slice

- Updated: 2026-09-19
- Review: When CLI commands, SDK or media dependencies change
- Owner: Implementation agent

Use .NET SDK 10.0.300 (latest patch roll-forward in that feature band) and standalone FFmpeg/FFprobe on PATH. The stdio host pins `ModelContextProtocol` 1.4.0; Python and Node.js are not needed. NativeAOT CLI publishing additionally requires the Windows native build toolchain. Executable overrides are `ROUGHCUT_FFMPEG` and `ROUGHCUT_FFPROBE`; these are machine configuration, not project JSON.

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
```

`frame` writes a source PNG and prints JSON metadata. `timeline-frame` requires the exact saved revision, resolves a timeline time through cuts/reordering/crops/image holds, writes the rendered PNG and prints its clip/source mapping. Seconds use invariant decimal notation with up to six fractional digits. The API uses integer ticks and rational time bases. A gap, out-of-range request or stale revision fails. MCP equivalents return PNG bytes directly as image content blocks; see the [stdio host guide](mcp.md).

To update safely, use `edit project.json operations.json <previous-revision>` for transactional trim/split/remove/reorder/crop/image-insert operations. Alternatively copy the JSON to a candidate, edit it and advance `revision` by one, then use `save candidate.json project.json <previous-revision>`. Preserve `projectId`. The timeline array defines clip order; video `in`/`out` values are source-relative ticks. Image clips use `[0, hold-duration)`, `contain` or `cover` fit, and explicit `audio: "silence"`. Active images require explicit encoded export. `map` calculates output intervals without executing edits.

The new `captions`, `preflight` and `export` commands are documented in the [bounded export guide](export.md), including the supported matrix, encoding policy, retimed SRT and validation report. Default export refuses encoding; `--allow-encode` permits it only in an encoding-enabled project mode. Exports use a new destination directory and never overwrite an existing bundle.

Stores reject unknown JSON fields rather than silently erase unsupported features. They cap documents at 4 MiB, validate references/ranges, serialize cooperating writers with an adjacent `.lock` file, flush a same-directory temporary file, then rename it over the previous revision. The lock file remains as a synchronization point. Lock contention is an explicit failure; reload/retry. External editors must use the save command to participate in revision checking. Network filesystem and power-loss durability have not been verified.

## Debug and publish

Open the repository folder in VS Code, select **Run and Debug**, choose a launch option and press **F5**. The workspace recommends Microsoft's C# extension (`ms-dotnettools.csharp`), which supplies the `coreclr` debugger; it is not installed automatically by this setup. Every launch builds the solution in Debug configuration first and uses the workspace root as its working directory.

Available options: CLI help, contract checks, media/CLI/MCP checks, stdio MCP host, inspect video, extract source or revision-aware timeline frames, create project, validate project, map timeline, save project revision, apply edit operations, import SRT captions, preflight export, export without encoding and export allowing encoding. Command launches prompt for paths, timestamps and revision values as needed. Paths may be absolute or workspace-relative; enter paths with spaces without surrounding quotes. The example defaults require your own local clip at `artifacts/demo/media/source.mkv`; the protocol checks generate their own synthetic fixtures. The sample edit file assumes a 1/1000 time base and a source at least three seconds long. Frame/new-project paths and export destination directories must not already exist. Save requires an edited candidate with the same project ID and the next revision.

Set breakpoints in the CLI or shared libraries when using a command launch. The media/CLI/MCP checks launch child processes, so use a direct command launch to debug inside those calls. The MCP launch prompts for an isolated workspace root and waits for protocol input on stdio; normally an MCP client launches the host itself. FFmpeg/FFprobe are needed for media operations; executable overrides are inherited from the VS Code environment. Build targets and DLL paths were exercised; an interactive F5/debugger session has not been tested.

```powershell
dotnet publish src/RoughCut.Cli -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish/win-x64
.\artifacts\publish\win-x64\roughcut.exe help
```

The Windows NativeAOT executable is self-contained for managed code. FFmpeg and FFprobe remain external executables. Publish output includes development symbols; they are not intended release assets. No FFmpeg redistribution or license selection has been made. Linux publishing/execution remains unverified.

## Implementation references

The adapter uses documented [FFprobe stream/frame JSON output](https://ffmpeg.org/ffprobe.html), [FFmpeg frame selection](https://ffmpeg.org/ffmpeg-filters.html#select_002c-aselect), and [concat demuxing](https://ffmpeg.org/ffmpeg-formats.html#concat). The strict-copy proof checks complete datastream structure from the [PNG specification](https://www.w3.org/TR/png-3/), alongside timestamp and decoded-output checks. Persistence uses [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) with reflection disabled. References checked 2026-09-19; recheck before dependency/tool changes. Actual execution evidence takes precedence over assumed compatibility.
