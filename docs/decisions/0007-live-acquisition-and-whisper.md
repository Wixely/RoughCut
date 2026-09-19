# 0007: Adopt explicit Deno acquisition and timed Whisper.net local STT on Windows

- Date: 2026-09-19
- Status: Accepted for the bounded Windows MVP
- Supersedes: Provider-adoption deferral in [0006](0006-acquisition-caption-speech-boundary.md)
- Review trigger: yt-dlp, Deno, Whisper.net/runtime or model update; Linux qualification; language expansion; NativeAOT speech evaluation; release dependency/license review

## Context

Wixely approved standalone Deno and the Bantz-style CPU Whisper.net/whisper.cpp backend with the base.en model. Bantz's public transcription contract combines text and discards native segment times, while RoughCut requires timed source intervals.

## Decision

Pass a caller-supplied Deno executable explicitly to yt-dlp, while retaining ignored user configuration, disabled remote components, no-playlist operation, staging and size/file-count bounds. Limit automatic acquisition captions to English for this MVP so one source cannot fan out into hundreds of translated caption downloads.

Use `Whisper.net` and `Whisper.net.Runtime` 1.9.1 directly in a separate speech provider project. Pin the base.en model by size and SHA-256. Preserve Whisper segment times, bound them to the supplied PCM chunk, persist content-addressed SRT plus source/model/chunk provenance, and expose the operation through a separate speech CLI and configured stdio MCP tool. Keep the main CLI free of the native runtime so its Windows NativeAOT publish remains independently testable.

## Consequences

Windows can acquire a current YouTube source through yt-dlp's Deno JavaScript challenge path and can transcribe media locally in sequential 5–30 second chunks. The speech CLI and MCP host are normal .NET deployments with native Whisper assets. Bantz remains unchanged.

The model-preparation command uses network access when the pinned model is absent; inference itself stays local. English-only acquisition/base.en transcription, Linux native loading, multi-hour resource behavior, dependency-license release review, diarization and Qwen TTS remain separate acceptance work.

## Evidence

See [RC-03 live evidence](../evidence/2026-09-19-rc03-live.md), the [acquisition/speech guide](../acquisition-and-speech.md) and the executable checks in `RoughCut.Tests`.
