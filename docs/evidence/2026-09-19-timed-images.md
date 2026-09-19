# Timed image workflow evidence — 2026-09-19

Environment: Windows, .NET SDK 10.0.300, FFmpeg/FFprobe `2026-04-01-git-eedf8f0165-full_build`, `ModelContextProtocol` 1.4.0.

Command:

```powershell
.\scripts\verify.ps1 -PublishAot
```

Observed: both managed and published Windows x64 NativeAOT CLI runs passed all 33 executable checks. The solution build completed with zero warnings or errors. The managed stdio MCP host was exercised through the official client in both runs.

The synthetic timed-image check imported an 80x96 RGB PNG into a 160x96, 10 fps, 48 kHz project; inserted a one-second image clip between reordered source clips; and confirmed revision-aware timeline selection. Pixel decoding distinguished black-padded `contain` output from cropped `cover` output. Strict copy-only preflight rejected the active image, while explicit encoding produced 10 repeated FFV1 frames and 48,000 PCM silence samples for the inserted interval. Complete export validation observed 30 video frames, 144,000 total audio samples, shifted captions, matching preview/export pixels and rejection after the image file changed.

The MCP checks verified the 12-tool catalogue, imported and inserted the PNG, received and decoded its revision-aware timeline preview, rejected a stale revision, completed and reloaded a validated image export job, then cancelled a separate job without publishing a bundle.

Limits: fixtures were synthetic and local. The source canvas was constant-rate SDR with square pixels and PCM audio. Image-only timelines, overlays, transitions, alpha compositing, JPEG/WebP, arbitrary unaligned hold durations, Linux, NativeAOT MCP publication and interactive AI-agent visual interpretation were not tested.
