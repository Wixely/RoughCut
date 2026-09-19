# Desktop review evidence — 2026-09-19

## Scope and environment

Windows PowerShell 5.1, .NET SDK 10.0.300 and the repository's existing FFmpeg/FFprobe adapter were used. No Python or Node.js tooling was used. The sibling CupriFace source was inspected read-only. RoughCut uses the official CupriFace 0.26.1 and CupriFace.Shell 0.26.1 release packages recorded in `vendor/nuget/README.md`.

The two package SHA-256 values are:

- `CupriFace.0.26.1.nupkg`: `e24aab209619d176da17990948c00f5283ba392b37dda5ad185915e17db02f40`
- `CupriFace.Shell.0.26.1.nupkg`: `46f0e7c5e8476711dd63514ec10eccda45364411482f489eaf422f2df2fe1620`

Package entries, NuGet metadata, license and repository provenance were inspected. Both declare MIT; metadata identifies source commit `a91ab263697bc32eab4aece52e3d71b6bd692017`.

## Executed results

`dotnet format RoughCut.slnx` completed, then:

```powershell
.\scripts\verify.ps1 -PublishAot
```

The Debug solution build completed with zero warnings and zero errors. The managed path passed 51 checks and the repeated path using the published Windows x64 NativeAOT main CLI passed 51 checks. New coverage verifies:

- speaker rename, persisted undo and persisted redo advance revisions and correction history;
- a 1280 by 800 CupriFace document renders from the session model;
- a separate desktop process resolves a real synthetic timeline through the exact project revision, decodes its retained video frame and writes a valid 1280 by 800 PNG larger than 10 KiB.

The desktop was also published and executed independently:

```powershell
dotnet publish src/RoughCut.Desktop -c Release -r win-x64 --self-contained false -o artifacts/publish/desktop-win-x64
.\artifacts\publish\desktop-win-x64\roughcut-desktop.exe snapshot <synthetic-project.json> <new-output.png>
```

The published process produced a 1280 by 800, 64,658-byte PNG. A richer ignored synthetic project containing two speakers, overlap, transcript rows and editorial evidence was rendered and visually inspected during development; the preview, panel, timeline and evidence layout were readable without overlap.

## Playback finding and limits

The sibling CupriFace MediaProbe sample built, but against that source checkout it reported that the `cupricodecs` native library was not loadable. This is evidence only for the inspected checkout and environment. RoughCut therefore uses its validated FFmpeg exact-frame path for this slice and makes no live playback/audio-sync claim.

No interactive window or VS Code debugger session was executed. Frame decoding remains synchronous in the click handler. Continuous video/audio playback, scrub performance, crop manipulation, live Windows interaction, Linux execution and desktop NativeAOT remain unverified.
