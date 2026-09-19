# 0006: Bound acquisition, caption selection and timed local speech behind adapters

- Date: 2026-09-19
- Status: Accepted deterministic RC-03 foundation; provider adoption pending
- Review trigger: native STT/provider adoption, yt-dlp/Deno installation, hosted URL intake, caption ranking changes or non-SRT input

## Decision

Invoke a user-configured standalone yt-dlp through a bounded C# adapter with fixed staging paths, ignored user configuration, no playlists, no shell command construction, explicit limits and an atomic portable manifest. Permit only an explicit Deno path for yt-dlp JavaScript execution; do not install or enable Node.js or remote components.

Assess caption candidates using locally verifiable provenance, language and source-time coverage. Persist the recommendation or explicit override with the selected track and advance the project revision atomically.

Keep local speech behind `ILocalSpeechTranscriber`. Normalize source audio to bounded 16 kHz mono PCM chunks and require the provider to return timed segments plus provider/model/language provenance. Do not reference Bantz source directly or adopt its native Whisper runtime until the dependency choice is approved and timed results are available.

## Consequences

CLI and MCP can acquire media/subtitles and choose captions without UI prompts. Fake-tool execution proves staging, limits and manifest behavior without network access. The speech pipeline can be tested and consumed independently of one model, while current Bantz dictation consumers remain untouched.

This decision does not claim live-site compatibility, YouTube JavaScript support, STT accuracy, model availability, Linux behavior, authentication/cookies, redirect/private-network safety for a hosted service or Bantz timed-segment compatibility.

## Evidence

See [RC-03 evidence](../evidence/2026-09-19-rc03-foundation.md) and the [acquisition/speech guide](../acquisition-and-speech.md).
