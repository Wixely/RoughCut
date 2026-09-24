# Published Windows outputs evidence

- Date: 2026-09-24
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, FFmpeg 2026-09-17-git-7070fe638e (gyan full build)
- Scope: RC-07 — running the regression suite against published executables rather than the build output
- Repeat with: `.\scripts\verify.ps1 -Published`

Everything verified until now ran from `bin\Debug`. Publishing is where deployment goes wrong: native assets that resolved from a build tree stop resolving, embedded resources go missing, and a host that started under `dotnet run` fails as an executable. This is the first run against what would actually be handed to someone.

## What was published and what passed

| Output | Form | Size | Result |
| --- | --- | --- | --- |
| `roughcut.exe` | Release, framework-dependent | 1.8 MB, 11 files | `76 passed; 0 failed` |
| `roughcut-desktop.exe` | Release, framework-dependent | 172 MB, 79 files | in the same run |
| `roughcut-mcp.exe` | Release, framework-dependent | 274 MB, 110 files | in the same run |
| `roughcut-desktop.exe` | Release, **self-contained** win-x64 | 102 MB | `76 passed; 0 failed` in a second full run |

The suite drives these as a person would: the CLI checks run the published `roughcut.exe`, the MCP checks launch the published host over stdio and speak the protocol to it, and the desktop checks run the published window headlessly. Both desktop forms were run through the whole suite.

## What that actually proves

The published desktop **decoded video and audio from its own layout**: `probe-playback` reports `playback ok: 1.003s, 10 frames, drift 0.0 ms, 0 underruns` from the self-contained build. That exercises the native decoders and the SDL audio sink loading out of a published `runtimes` tree, which is the failure this run was looking for.

The published window also renders its review surface headlessly from an unrelated working directory, so its embedded icon and fonts resolve outside the build tree, and the published CLI resolves FFmpeg, FFprobe, yt-dlp and Deno through the settings file from that same unrelated directory — deployment does not depend on the current directory being the repository.

The self-contained desktop build needs no .NET installed at all, and it passed the same suite as the framework-dependent one.

## One defect found, in the locking rather than the outputs

The self-contained publish rewrote four lock files. A lock file is per framework and runtime identifier, so a `-r win-x64` publish of a project declaring no runtimes adds a `net10.0/win-x64` section, and the next ordinary locked restore refuses it with NU1004 — which is exactly what `verify.ps1 -Published` did on its first run, two steps after doing the publishing itself. The build now declares `win-x64` as a runtime identifier, so one lock file covers both shapes; a locked restore taken after a self-contained publish is clean, and the whole script passes end to end.

This would have reached a build machine as an intermittent failure depending on what had been published last.

## Limitations

Windows x64 is the only platform anything was executed on. `linux-x64` is declared and locked, and the CLI and desktop publish for it carrying their Linux native assets, but nothing was run: that is a buildable starting point, not a Linux result. macOS has not been attempted, and neither has an ARM64 build of anything.

The NativeAOT CLI path could not be rebuilt: the MSVC platform linker is absent on this machine. The last NativeAOT run passed 56 checks on an earlier slice, and that number has not moved since.

Publishing was checked for the three hosts that have entry points. The MCP host remains a managed deployment by design — its reflection-based schema generation and the native speech runtimes are not qualified for NativeAOT.

No installer, signing, or update mechanism exists, and none was attempted. A published folder is exactly that: a folder.
