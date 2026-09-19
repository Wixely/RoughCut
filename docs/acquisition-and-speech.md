# Acquisition, caption selection and local speech boundary

- Updated: 2026-09-19
- Owner: Implementation agent
- Review: Before adopting a native STT provider, JavaScript runtime, new caption format or server-side URL intake
- Status: Live yt-dlp/Deno and Whisper.net base.en acceptance verified on Windows

## URL acquisition

`acquire` invokes a configured standalone yt-dlp executable directly with argument arrays. It ignores user configuration, accepts one HTTP(S) URL without embedded credentials, disables playlists, caps the media at 512 MiB and the staged result at 600 MiB/64 files, requests English manual and automatic subtitles, converts the selected subtitle representation to SRT, and writes fixed filenames into a unique staging directory. A successful run records hashes and bounded metadata in `acquisition.json`, strips URL query/fragment data from provenance, and publishes by directory rename. Cancellation and failure do not publish the destination. English-only acquisition is the current MVP bound; supplied captions and later acquisition policies can cover other languages.

```powershell
$env:ROUGHCUT_YTDLP = 'C:\tools\yt-dlp.exe'
dotnet run --project src/RoughCut.Cli -- acquire https://example.com/video artifacts/acquired/video
```

An optional third argument supplies a Deno executable and becomes an explicit `--js-runtimes deno:<path>` setting. RoughCut never enables Node.js or remote yt-dlp components and does not install a runtime. The accepted Windows configuration uses standalone Deno 2.9.7. Current yt-dlp documentation says full YouTube support needs yt-dlp-ejs plus a supported JavaScript runtime and recommends Deno; official standalone yt-dlp builds include the EJS component. Recheck [yt-dlp requirements](https://github.com/yt-dlp/yt-dlp#dependencies) and the [EJS guide](https://github.com/yt-dlp/yt-dlp/wiki/EJS) when updating either executable.

The current adapter is intended for a trusted local user or MCP client. It does not provide the private-address/redirect/cookie policy needed for a hosted URL-ingestion service.

## Caption assessment

`captions-select` reads 1–32 project-local SRT candidates from JSON, rejects malformed/out-of-range candidates, calculates union coverage in source time, and records cue count, language match and provenance. Ranking is deterministic: manual captions precede supplied captions, automatic captions and local STT, with preferred-language and coverage adjustments. These scores compare candidates inside RoughCut; they do not treat provider confidence values as interchangeable.

```powershell
dotnet run --project src/RoughCut.Cli -- captions-select artifacts/demo/project.json source-1 examples/caption-candidates.json 1 en
dotnet run --project src/RoughCut.Cli -- captions-select artifacts/demo/project.json source-1 examples/caption-candidates.json 1 en automatic-en
```

The optional final argument is an explicit override. Selection advances the revision atomically and stores source kind, language, selection policy, candidate ID and coverage in the caption track. MCP exposes the same operation as `roughcut_select_captions`.

## Local Whisper transcription

`ILocalSpeechTranscriber` accepts one bounded signed 16-bit, 16 kHz mono PCM chunk and returns provider/model/language provenance plus millisecond-relative timed segments. `LocalSpeechProcessor` verifies the source hash, rejects sources over two hours, extracts sequential 5–30 second chunks through FFmpeg with bounded output and subprocess lifetime, validates every result, maps segments back into exact project time and rechecks the source hash. Small resampler rounding gaps of at most one millisecond are zero-padded; larger or oversized output is rejected.

RoughCut adopts `Whisper.net` and its CPU runtime at version 1.9.1 in a separate normal-.NET speech project. The provider downloads and verifies the pinned base.en model (147,964,211 bytes; SHA-256 `A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002`), preserves native segment timing, and clamps a provider segment only to the exact PCM boundary. The main CLI remains NativeAOT-capable; the speech CLI and MCP host use normal .NET because Whisper loads native assets.

```powershell
dotnet run --project src/RoughCut.Speech.Cli -- model artifacts/models/ggml-base.en.bin
dotnet run --project src/RoughCut.Speech.Cli -- transcribe artifacts/demo/project.json source-1 1 artifacts/models/ggml-base.en.bin en 30
```

The operation writes a content-addressed SRT, timed speech segments and model/source/chunk provenance in one revision-safe project update. For MCP, configure `ROUGHCUT_STT_MODEL`; optional `ROUGHCUT_STT_LANGUAGE` and `ROUGHCUT_STT_CHUNK_SECONDS` override `en` and 30 seconds.

The sibling Bantz repository was inspected at commit `e930c91`. Its public result discards Whisper segment times, so RoughCut uses the same Whisper.net backend directly and leaves Bantz unchanged.

The accepted dependency and live results are recorded in [decision 0007](decisions/0007-live-acquisition-and-whisper.md) and [RC-03 live evidence](evidence/2026-09-19-rc03-live.md). Linux, non-English acquisition/transcription, hosted URL security and multi-hour resource profiling remain unverified.
