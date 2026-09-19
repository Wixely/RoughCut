# Desktop playback evidence — 2026-09-19

## Scope and dependency

The official CupriFace.Media 0.26.1 release package was downloaded from the same public release as the existing CupriFace packages. Its SHA-256 is `b5581765112cdef3c4f5c5f384a7249cd6ecf81b6a73f59b391e9260a3a68aef`. Package metadata identifies source commit `a91ab263697bc32eab4aece52e3d71b6bd692017` and MIT licensing. The archive contains the managed media assembly and six native codec assets, including the 2,351,609-byte Windows x64 `cupricodecs.dll`.

RoughCut generated the playback input by running its bounded validated timeline export, transcoding the result to VP9/Opus WebM and checking the proxy codec, dimensions, duration and 128 MiB bound. The ignored cache key includes the exact project JSON hash and revision.

## Executed results

On Windows with .NET SDK 10.0.300 and the repository's recorded FFmpeg/FFprobe build:

```powershell
.\scripts\verify.ps1 -PublishAot
dotnet publish src/RoughCut.Desktop -c Release -r win-x64 --self-contained false -o artifacts/publish/desktop-win-x64
.\artifacts\publish\desktop-win-x64\roughcut-desktop.exe probe-playback <synthetic-project.json> 2.0
```

The managed path passed 52 checks. The repeated path using the published Windows x64 NativeAOT main CLI passed 52 checks. The framework-dependent Windows x64 desktop publish contained `CupriFace.Media.dll` and the 2,351,609-byte Windows x64 `cupricodecs.dll`.

The published probe generated a 117,764-byte proxy for the four-second synthetic project. With SDL's real-time dummy audio device it advanced to 2.005 seconds, decoded 20 video frames, measured 0.0 ms audio-lag growth and reported zero underruns. The UI snapshot was visually inspected at 1280 by 800 with the exact-frame poster plus play, mute, seek, duration and fullscreen controls.

## Limits

The SDL dummy device drains audio at wall-clock rate and proves the playback scheduling path without requiring speakers. It does not establish behavior on a physical Windows audio device. No interactive window/debugger session or Linux execution occurred. The proxy inherits the bounded export matrix and duration limit. Preparation is synchronous, and the native backend currently loads the complete proxy into memory.
