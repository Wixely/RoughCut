# RC-03 deterministic foundation evidence — 2026-09-19

Environment: Windows, .NET SDK 10.0.300, FFmpeg/FFprobe `2026-04-01-git-eedf8f0165-full_build`, `ModelContextProtocol` 1.4.0. Sibling Bantz source inspected at `e930c91` with a clean working tree.

Command:

```powershell
.\scripts\verify.ps1 -PublishAot
```

Observed: the managed and published Windows x64 NativeAOT CLI paths each passed 37 executable checks with zero build warnings/errors. The official MCP client discovered 14 tools and successfully selected a caption candidate with persisted provenance.

Deterministic acquisition used a fake external tool to exercise the exact yt-dlp argument policy and staged outputs without network access. It verified ignored user configuration, single-item/no-playlist policy, fixed filenames, bounded metadata/SRT parsing, media and caption hashes, manual-caption classification, query/fragment scrubbing, manifest publication and invalid-scheme rejection.

Caption fixtures proved manual-versus-automatic recommendation, language/coverage evidence, malformed-candidate reporting, revision-safe persistence, explicit override and stale-revision rejection through application, CLI and MCP paths.

Local speech processing decoded a 12-second 48 kHz fixture into three bounded 16 kHz mono PCM chunks, passed them through a timed fake local provider, mapped segments to `[0s,5s)`, `[5s,10s)` and `[10s,12s)`, retained provider/model/language provenance, and rejected a source beyond the two-hour policy.

Limits: yt-dlp was not found on PATH and no live URL was contacted. Deno, a native Whisper runtime and a model were not installed. No speech accuracy, real Bantz integration, Linux behavior, hosted URL security, long-duration wall-clock/resource behavior or translated/drifting-caption quality was established.
