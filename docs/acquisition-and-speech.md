# Acquisition, caption selection and local speech boundary

- Updated: 2026-09-27
- Owner: Implementation agent
- Review: Before adopting a native STT provider, JavaScript runtime, new caption format or server-side URL intake
- Status: Live yt-dlp/Deno and Whisper.net base.en acceptance verified on Windows

## URL acquisition

`acquire` invokes a configured standalone yt-dlp executable directly with argument arrays. It ignores user configuration, accepts one HTTP(S) URL without embedded credentials, disables playlists, caps the media at 512 MiB and the staged result at 600 MiB/64 files, requests English manual and automatic subtitles, converts the selected subtitle representation to SRT, and writes fixed filenames into a unique staging directory. A successful run records hashes and bounded metadata in `acquisition.json`, strips URL query/fragment data from provenance, and publishes by directory rename. Cancellation and failure do not publish the destination. English-only acquisition is the current MVP bound; supplied captions and later acquisition policies can cover other languages.

```powershell
$env:ROUGHCUT_YTDLP = 'C:\tools\yt-dlp.exe'
dotnet run --project src/RoughCut.Cli -- formats https://example.com/video
dotnet run --project src/RoughCut.Cli -- acquire https://example.com/video artifacts/acquired/video --format medium
dotnet run --project src/RoughCut.Cli -- create-url https://example.com/video artifacts/acquired/video
```

`create-url` runs the whole sequence in one bounded step: acquire the URL with its subtitles, create `project.json` beside the downloaded media, and select the best caption track with its provenance. It is the same `CreateProjectFromUrlAsync` behind the desktop launcher's **Fetch** button and the `roughcut_create_project_from_url` MCP tool, so no host has to compose acquire, create and caption selection by hand or reproduce the yt-dlp argument policy. A source with no usable subtitles still yields a valid project and reports the reason rather than failing.

### Choosing the rendition

`formats` reports what the source offers — identifier, extension, codecs, resolution, bitrate and size where the site states it — and `roughcut_list_source_formats` returns the same list. The caller then names a format identifier, two joined by `+`, or one of three policies: `highest`, `medium` or `lowest` by bitrate. `medium` is the default, because the largest rendition of an ordinary music video does not fit the 512 MiB media bound and the smallest is not worth cutting.

A picture-only rendition asked for on its own is refused, naming the pairing it needs: a silent download is far more likely a mistake than a request. A caller offering a list to a person pairs it first with `SourceFormatPolicy.WithAudio`, which is what the review window does.

A policy only ever chooses a rendition that fits that bound, and a rendition that cannot fit is refused before anything is downloaded rather than aborting part-way through. That failure is what made this necessary: the default selection took a 604 MiB 4K AV1 rendition, hit the cap mid-download, and left a `.part` file beside the audio — which acquisition then reported as "Sequence contains more than one matching element". A partial download is now named as one, and more than one candidate media file lists what it found.

### JavaScript runtime

yt-dlp has needed an external JavaScript runtime for full YouTube support since 2025.11.12. It is not a hard requirement: without one, format availability is limited — "severely so in some cases" — and is expected to worsen over time. Of the four supported runtimes yt-dlp enables **only Deno by default**, disabling Node, QuickJS and Bun for security reasons, and finds Deno on `PATH` or beside `yt-dlp.exe`. It never downloads or installs a runtime.

RoughCut therefore leaves that default alone. It does **not** send `--no-js-runtimes`, which would clear the default Deno entry and silently cost format availability even on a machine where Deno is installed; see [decision 0018](decisions/0018-default-javascript-runtime.md), which supersedes that element of [0007](decisions/0007-live-acquisition-and-whisper.md). An optional third CLI argument, or the shared tool configuration described in the [development guide](development.md), supplies a Deno executable and becomes an explicit `--js-runtimes deno:<path>` setting that pins exactly which binary runs. The recorded Windows configuration used standalone Deno 2.9.7.

What RoughCut does still refuse is remote code: `--no-remote-components` stops yt-dlp downloading EJS scripts from GitHub or npm at run time, which official standalone builds do not need because `yt-dlp-ejs` is bundled with them. Node.js is never enabled. Recheck the [yt-dlp requirements](https://github.com/yt-dlp/yt-dlp#dependencies), the [EJS guide](https://github.com/yt-dlp/yt-dlp/wiki/EJS) and the [runtime announcement](https://github.com/yt-dlp/yt-dlp/issues/15012) when updating either executable.

The current adapter is intended for a trusted local user or MCP client. It does not provide the private-address/redirect/cookie policy needed for a hosted URL-ingestion service.

## Caption assessment

`captions-select` reads 1–32 project-local SRT candidates from JSON, rejects malformed/out-of-range candidates, calculates union coverage in source time, and records cue count, language match and provenance. Ranking is deterministic: manual captions precede supplied captions, automatic captions and local STT, with preferred-language and coverage adjustments. These scores compare candidates inside RoughCut; they do not treat provider confidence values as interchangeable.

```powershell
dotnet run --project src/RoughCut.Cli -- captions-select artifacts/demo/project.json source-1 examples/caption-candidates.json 1 en
dotnet run --project src/RoughCut.Cli -- captions-select artifacts/demo/project.json source-1 examples/caption-candidates.json 1 en automatic-en
```

The optional final argument is an explicit override. Selection advances the revision atomically and stores source kind, language, selection policy, candidate ID and coverage in the caption track. MCP exposes the same operation as `roughcut_select_captions`.

## Local Whisper transcription

`ILocalSpeechTranscriber` accepts one bounded signed 16-bit, 16 kHz mono PCM chunk and returns provider/model/language provenance plus millisecond-relative timed segments. `LocalSpeechProcessor` verifies the source hash, rejects sources over two hours, extracts 5–30 second chunks through FFmpeg with bounded output and subprocess lifetime, validates every result, maps segments back into project time and rechecks the source hash.

Chunks **overlap** ([decision 0024](decisions/0024-overlapping-transcription-windows.md)). Each window reaches back three seconds before its chunk — `overlapSeconds` overrides it, up to half a chunk — and anything the model reports whose start falls inside that prefix is discarded and counted. A model handed a window with no history reports something at the start of it: on a real 13-minute source, isolated chunks invented a segment at 27 of the 28 boundaries, 22% of all the speech the transcript claimed. The prefix puts each boundary inside material the model has already heard. The project records `overlapSeconds` and `discardedOverlapSegments` beside the chunk size, so a transcript explains its own shape.

Resampler rounding gaps up to 250 ms are zero-padded and reported as `paddedMilliseconds`; larger or oversized output is rejected. The tolerance was one millisecond until a real source arrived 7.5 ms short on one chunk and aborted the whole transcription — a chunk boundary is not a claim about the source, so a small shortfall is padded and named rather than fatal.

RoughCut adopts `Whisper.net` and its CPU runtime at version 1.9.1 in a separate normal-.NET speech project. The provider downloads and verifies the pinned base.en model (147,964,211 bytes; SHA-256 `A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002`), preserves native segment timing, and clamps a provider segment only to the exact PCM boundary. The main CLI remains NativeAOT-capable; the speech CLI and MCP host use normal .NET because Whisper loads native assets.

```powershell
dotnet run --project src/RoughCut.Speech.Cli -- model artifacts/models/ggml-base.en.bin
dotnet run --project src/RoughCut.Speech.Cli -- transcribe artifacts/demo/project.json source-1 1 artifacts/models/ggml-base.en.bin en 30
```

The operation writes a content-addressed SRT, timed speech segments and model/source/chunk/overlap provenance in one revision-safe project update. For MCP, `ROUGHCUT_STT_MODEL` defaults to `%LOCALAPPDATA%\RoughCut\models\ggml-base.en.bin` so a host that has fetched the model once needs no configuration; optional `ROUGHCUT_STT_LANGUAGE` and `ROUGHCUT_STT_CHUNK_SECONDS` override `en` and 30 seconds. Transcription refuses when the model file is absent, naming `roughcut_fetch_speech_model`, rather than downloading 147 MB inside a transcribe.

Speech timings are a model's estimates, and they are converted with `TimeMath.NearestTicks`; a measured length uses `TimeMath.FloorTicks`. Neither demands an exact representation, because a source whose time base cannot express its own duration in whole milliseconds is ordinary, and demanding exactness there refused to transcribe it at all. An edit boundary, which decides which frames survive, still uses `TimeMath.ExactTicks`.

What transcription still gets wrong is the opposite error: `base.en` misses sung words it does not hear as speech, and no amount of surrounding context repairs a false negative. Cutting every transcribed segment out of a music video therefore leaves some vocal material behind.

The sibling Bantz repository was inspected at commit `e930c91`. Its public result discards Whisper segment times, so RoughCut uses the same Whisper.net backend directly and leaves Bantz unchanged.

The accepted dependency and live results are recorded in [decision 0007](decisions/0007-live-acquisition-and-whisper.md) and [RC-03 live evidence](evidence/2026-09-19-rc03-live.md). Linux, non-English acquisition/transcription, hosted URL security and multi-hour resource profiling remain unverified.
