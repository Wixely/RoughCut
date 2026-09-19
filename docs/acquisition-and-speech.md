# Acquisition, caption selection and local speech boundary

- Updated: 2026-09-19
- Owner: Implementation agent
- Review: Before adopting a native STT provider, JavaScript runtime, new caption format or server-side URL intake
- Status: Deterministic boundary verified on Windows; live yt-dlp and native Whisper acceptance pending

## URL acquisition

`acquire` invokes a configured standalone yt-dlp executable directly with argument arrays. It ignores user configuration, accepts one HTTP(S) URL without embedded credentials, disables playlists, caps the media at 512 MiB and the staged result at 600 MiB/64 files, requests manual and automatic subtitles, converts the selected subtitle representation to SRT, and writes fixed filenames into a unique staging directory. A successful run records hashes and bounded metadata in `acquisition.json`, strips URL query/fragment data from provenance, and publishes by directory rename. Cancellation and failure do not publish the destination.

```powershell
$env:ROUGHCUT_YTDLP = 'C:\tools\yt-dlp.exe'
dotnet run --project src/RoughCut.Cli -- acquire https://example.com/video artifacts/acquired/video
```

An optional third argument supplies a Deno executable and becomes an explicit `--js-runtimes deno:<path>` setting. RoughCut never enables Node.js or remote yt-dlp components and does not install a runtime. Current yt-dlp documentation says full YouTube support needs yt-dlp-ejs plus a supported JavaScript runtime and recommends Deno; official standalone yt-dlp builds include the EJS component. Recheck [yt-dlp requirements](https://github.com/yt-dlp/yt-dlp#dependencies) and the [EJS guide](https://github.com/yt-dlp/yt-dlp/wiki/EJS) before adoption.

The current adapter is intended for a trusted local user or MCP client. It does not provide the private-address/redirect/cookie policy needed for a hosted URL-ingestion service.

## Caption assessment

`captions-select` reads 1–32 project-local SRT candidates from JSON, rejects malformed/out-of-range candidates, calculates union coverage in source time, and records cue count, language match and provenance. Ranking is deterministic: manual captions precede supplied captions, automatic captions and local STT, with preferred-language and coverage adjustments. These scores compare candidates inside RoughCut; they do not treat provider confidence values as interchangeable.

```powershell
dotnet run --project src/RoughCut.Cli -- captions-select artifacts/demo/project.json source-1 examples/caption-candidates.json 1 en
dotnet run --project src/RoughCut.Cli -- captions-select artifacts/demo/project.json source-1 examples/caption-candidates.json 1 en automatic-en
```

The optional final argument is an explicit override. Selection advances the revision atomically and stores source kind, language, selection policy, candidate ID and coverage in the caption track. MCP exposes the same operation as `roughcut_select_captions`.

## Local speech boundary and Bantz evaluation

`ILocalSpeechTranscriber` accepts one bounded signed 16-bit, 16 kHz mono PCM chunk and returns provider/model/language provenance plus millisecond-relative timed segments. `LocalSpeechProcessor` verifies the source hash, rejects sources over two hours, extracts sequential 5–30 second chunks through FFmpeg with bounded output and subprocess lifetime, validates every result, maps segments back into exact project time and rechecks the source hash.

The sibling Bantz repository was inspected at commit `e930c91`. Its reusable `Bantz.Speech.Abstractions` package accepts the same PCM shape, and its Whisper engine processes native Whisper segments, but `TranscriptionResult` currently exposes only combined text and discards segment times. RoughCut therefore has a compatible provider boundary without a Bantz project reference or copied native dependency. A Bantz adapter can map a whole chunk coarsely today; production timed captions require a backward-compatible timed result in Bantz or another local provider. Bantz's existing consumers were not changed.

No model, Whisper native runtime, yt-dlp executable or Deno runtime was installed by this slice. Live provider acceptance remains blocked on those explicit dependency choices.
