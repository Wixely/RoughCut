# Two products, one library

- Added: 2026-09-28
- Owner: Implementation agent
- Review: When a shell gains logic of its own, or a third interface is added

RoughCut ships as two things people install, built from the same code:

- **`roughcut-mcp`** — the MCP server. It serves the estate's HTTP transport on port 5722 for an MCP hub to manage, and the stdio transport a client launches for itself.
- **`roughcut-desktop`** — the window a person opens to make or change an edit.

They are not two products that happen to look alike. They are two shells over one library, and that is what
lets an agent and a person work on the same project at the same time — the window follows an agent's saved
revision, and both mean exactly the same thing by a cut, a format and a folder.

## What is shared and what is not

```
RoughCut.Core         contracts, validation, edit engine, time arithmetic   1,240 lines, no packages
  └─ RoughCut.Media   FFmpeg: reading, frames, strict export, delivery,     2,200 lines, no packages
     │                mux, audio profiling
     └─ RoughCut.Application   projects, acquisition, captions, speech,     2,100 lines, no packages
        │                      export jobs, folders, format policy
        ├─ RoughCut.Cli        ~450 lines   → roughcut.exe          NativeAOT-capable
        ├─ RoughCut.Mcp        ~560 lines   → roughcut-mcp.exe      DnaX.MCPFab (estate host), ASP.NET Core
        └─ RoughCut.Desktop    ~3,400 lines → roughcut-desktop.exe  CupriFace shell, media, decoders
```

No shell references another. The MCP host does not pull in the window's shell or its decoders, and the window
does not pull in the MCP host — so the server an agent launches carries nothing it cannot use.

**A shell may decide how to ask and how to show. It may not decide what something means.** Which rendition to
offer, what an export format name resolves to, where an edit lives, how a folder is named — all of that is in
`RoughCut.Application`, because a second interface would otherwise have to reinvent it and would drift. Two
things that had drifted this way are worth remembering: the command line kept its own copy of project creation
and went on indexing every frame of a source long after the shared path stopped, and the window kept its own
rendition-presentation rules where nothing else could reach them.

The speech and diarization providers are separate projects (`RoughCut.Speech.Whisper`, `RoughCut.Speech.Sherpa`)
because they carry native runtimes. The MCP host references them; the window does not, so a person who only
wants to cut video does not carry a speech model's runtime.

## Building the artefacts

```powershell
.\scripts\package.ps1                     # both, framework-dependent (.NET 10 required on the machine)
.\scripts\package.ps1 -SelfContained      # both, carrying their own runtime
.\scripts\package.ps1 -Only mcp           # just the server
```

Output lands in `artifacts/package/<version>/`, with a `checksums.txt` of every published binary. The version
comes from `git describe` unless `-Version` says otherwise, so an untagged build is named after the commit it
came from. The script restores with `--locked-mode` first: a build whose packages drifted is not the product
that was verified.

Framework-dependent sizes as of this writing: `roughcut-mcp` 44.8 MB — most of it the Whisper and sherpa-onnx
native runtimes — and `roughcut-desktop` 25.9 MB.

Neither is built with NativeAOT. The MCP host builds its tool schema by reflection and loads native speech
runtimes; the desktop shell loads native decoders. `roughcut.exe`, the command line, is the one that publishes
ahead of time — see the [development guide](development.md).

## The estate's shared host

`roughcut-mcp` runs on `DnaX.MCPFab`, the host every `*MCPSharp` server in this estate shares, so a hub
manages it the same way it manages the others: one process per server, HTTP on a registered port, `/healthz`
to probe, Serilog to the same file shape, a startup banner, Windows service detection and the same shutdown
path. Only the tools and the health payload are RoughCut's own.

| | |
| --- | --- |
| Product | `RoughCutMCPSharp` |
| Port | **5722** — the estate assigns 5700–5721 to other servers |
| Endpoint | `http://localhost:5722/mcp`, health at `/healthz` |
| Configuration | `RoughCutMCPSharp.json` beside the executable, then `ROUGHCUTMCP_`-prefixed environment variables, then the command line |
| Logs | `roughcutmcp` file prefix |

The package comes from the authenticated `GitHub-Wixely-Packages` feed, source-mapped to `DnaX.*` in
`NuGet.config`. Only this project needs those credentials: the libraries, the command line and the desktop
window all restore from nuget.org, so a contributor without the token can still build and run everything else.

RoughCut keeps its own build discipline rather than adopting the estate's `Directory.Build.props`: committed
lock files, `TreatWarningsAsErrors`, and a NativeAOT command line. The estate's central package versions and
trim tiers are a repository-wide switch, and turning them on here would apply trim analysis to a desktop
shell full of native decoders. That is a deliberate difference, not an oversight.

## Registering the server with an MCP hub

Everything the server touches is inside one workspace root: absolute paths, `..` and reparse points are
refused. A hub that manages processes launches it on its port:

```json
{
  "roughcut": {
    "command": "C:\\Tools\\roughcut\\roughcut-mcp.exe",
    "args": ["--workspace", "C:\\Users\\me\\Videos\\RoughCut"],
    "url": "http://127.0.0.1:5722/mcp",
    "healthUrl": "http://127.0.0.1:5722/healthz"
  }
}
```

A client that launches the server itself, rather than connecting to one already running, asks for the other
transport with `--stdio`:

```json
{
  "mcpServers": {
    "roughcut": {
      "command": "C:\\Tools\\roughcut\\roughcut-mcp.exe",
      "args": ["--stdio", "--workspace", "C:\\Users\\me\\Videos\\RoughCut"],
      "env": {
        "ROUGHCUT_FFMPEG": "C:\\Tools\\ffmpeg\\ffmpeg.exe",
        "ROUGHCUT_FFPROBE": "C:\\Tools\\ffmpeg\\ffprobe.exe",
        "ROUGHCUT_YTDLP": "C:\\Tools\\yt-dlp\\yt-dlp.exe"
      }
    }
  }
}
```

`ROUGHCUT_WORKSPACE` can supply the root instead of the argument. FFmpeg, FFprobe, yt-dlp and Deno resolve
through the order in the [development guide](development.md) — environment variable, then the tools settings
file, then `PATH` — so the `env` block above is only needed where they are not on `PATH`. Speech needs a model:
`roughcut_fetch_speech_model` fetches the pinned one, and transcription refuses until it is there rather than
downloading it unannounced. The [MCP guide](mcp.md) lists every tool.

Point the window at the same workspace and the two work together: an agent's `roughcut_apply_edits` appears in
the open window without anyone pressing Reload.

## Limitations

Only `win-x64` is exercised. `linux-x64` is declared, locked and builds with its native assets, but has never
been run; macOS is neither declared nor built. There is no installer, no code signing and no release workflow —
`package.ps1` produces a folder, and putting it somewhere is still a manual step. The checksums it writes are
for recognising an artefact, not for verifying provenance: nothing signs them.
