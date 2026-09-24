# Dependencies, licences and assets

- Added: 2026-09-24
- Owner: Implementation agent
- Review: Whenever a package, native asset, external tool or model changes
- Status: Inventory taken on 2026-09-24 from the committed lock files; regenerate with `.\scripts\dependency-report.ps1`

RoughCut depends on three different kinds of thing, and confuses them at its peril: **managed packages** it builds against and ships, **native assets** those packages carry into the output, and **external tools and models** it merely runs or reads, and never distributes.

## Pinning

Every direct `PackageReference` uses an exact version range — `[1.4.0]`, not `1.4.0`, which NuGet reads as a floor — and `RestorePackagesWithLockFile` is on, so each project has a committed `packages.lock.json` recording the whole resolved graph with content hashes. The lock files are the authoritative record; this page is a summary of them.

```powershell
dotnet restore RoughCut.slnx --locked-mode   # resolve exactly what is committed, or fail
.\scripts\dependency-report.ps1 -Detailed    # every resolved package, version and licence
```

`--locked-mode` is what a build machine should use: it refuses to resolve anything the lock files do not name, so a dependency cannot drift silently between machines. Changing a dependency therefore takes two deliberate steps — edit the version, run a normal `dotnet restore` to update the lock files — and both land in the same commit.

The SDK is pinned in `global.json` to 10.0.300 with `rollForward: latestPatch`.

## Managed packages

Nine direct references, resolving to 83 packages in total. Every one carries a permissive licence, which the report asserts: MIT for 69, Apache-2.0 for 12, Zlib for 2.

| Direct package | Version | Licence | Used for |
| --- | --- | --- | --- |
| `CupriFace` | 0.26.1 | MIT | The desktop review window's HTML/CSS engine |
| `CupriFace.Media` | 0.26.1 | MIT | VP9/Opus playback in that window |
| `CupriFace.Shell` | 0.26.1 | MIT | The desktop window, input and cursors |
| `ModelContextProtocol` | 1.4.0 | Apache-2.0 | The stdio MCP host |
| `Microsoft.Extensions.Hosting` | 10.0.9 | MIT | Hosting for that MCP process |
| `Whisper.net` | 1.9.1 | MIT | Local transcription |
| `Whisper.net.Runtime` | 1.9.1 | MIT | Its native whisper.cpp builds |
| `org.k2fsa.sherpa.onnx` | 1.13.8 | Apache-2.0 | Local speaker diarization |
| `AngleSharp`, `SkiaSharp`, `HarfBuzzSharp`, `Silk.NET.*`, `Microsoft.Extensions.*` | see lock files | MIT | Pulled in transitively by the above |

The three CupriFace packages are **vendored** under `vendor/nuget` rather than restored from a public feed, with their SHA-256 hashes and source release recorded in [that folder's README](../vendor/nuget/README.md). The rest come from nuget.org.

Two packages declare their licence as a packaged file rather than an SPDX expression — `Whisper.net` and `Microsoft.DotNet.PlatformAbstractions`. Both files are the MIT licence; the report reads them and says so rather than reporting a filename.

Apache-2.0 carries attribution and NOTICE obligations that MIT does not. Nothing in this repository redistributes those packages today — it builds from them — but a release that bundles the MCP host or the Sherpa runtime must carry their notices.

## Native assets in the build output

These arrive inside the managed packages above and are copied next to the executables, one set per platform under `runtimes/`:

| Asset | From | Licence | Needed by |
| --- | --- | --- | --- |
| `libSkiaSharp`, `libHarfBuzzSharp` | SkiaSharp, HarfBuzzSharp | MIT | All rendering and text shaping |
| `SDL2`, `glfw3` | Ultz.Native.SDL, Ultz.Native.GLFW | Zlib | The desktop window |
| `cupricodecs` | CupriFace.Media | MIT | VP9/Opus decoding for playback |
| `whisper`, `ggml-*` | Whisper.net.Runtime | MIT | Local transcription |
| `sherpa-onnx-c-api`, `onnxruntime` | org.k2fsa.sherpa.onnx runtime packages | Apache-2.0, MIT | Local diarization |

The CLI is the only project that has been published NativeAOT, and that path cannot be rebuilt on the current verification machine because the MSVC platform linker is absent. The MCP host is a normal managed deployment: its reflection-based schema generation and these native speech runtimes have not been qualified for NativeAOT.

## External tools, never distributed

RoughCut runs these as separate processes and ships none of them. They are resolved through the order in the [development guide](development.md) — explicit argument, environment variable, tools settings file, then `PATH` — and `roughcut tools` prints what resolves.

| Tool | Why | Licence note |
| --- | --- | --- |
| FFmpeg, FFprobe | Every frame, probe, export and delivery | Builds differ: the common Windows builds are GPL, others LGPL. Anyone packaging RoughCut *with* FFmpeg takes on that licence; invoking an installed binary does not |
| yt-dlp | URL acquisition with subtitles | Unlicense |
| Deno | The JavaScript runtime yt-dlp uses by default for some sites, per [decision 0018](decisions/0018-default-javascript-runtime.md) | MIT |

## Models, never distributed

| Model | Supplied by | Note |
| --- | --- | --- |
| Whisper `base.en` (ggml) | Downloaded by the user or the runtime | The pinned provider for local transcription; other sizes and languages are unverified |
| sherpa-onnx segmentation and embedding models | The user, configured explicitly | Absent configuration is reported safely rather than guessed |
| Qwen TTS | A loopback service the user runs | RoughCut never reaches beyond loopback for it |

No model weights are in this repository, and none are downloaded without the user asking for the operation that needs them.

## Keeping this honest

Run `.\scripts\dependency-report.ps1` after any dependency change. It reads the lock files and the local package cache, prints every resolved package with its licence, and exits nonzero if anything is not on the permissive list — which is the point at which a licence becomes a decision rather than a detail, and belongs in a decision record.
