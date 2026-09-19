# Bounded voice-rendering evidence

- Date: 2026-09-19
- Scope: Exact sample fitting and encoded replacement rendering on Windows
- Review trigger: Fit limits, FFmpeg behavior, source/audio format matrix, background handling or replacement mapping changes

## Synthetic regression

The existing four-second 160x96 PNG/48 kHz mono PCM fixture received two adjacent corrected speech intervals of 0.500 seconds. Both reused one speaker mapping and a zero-valued 24 kHz mono WAVE preview lasting 0.520 seconds. The `time-stretch` policy produced exactly 24,000 target samples for each interval. Export kept the independently decodable PNG video packets copied, encoded the complete PCM audio timeline, retained the original changing-frequency samples outside the intervals and replaced only the selected intervals.

Preflight rejected strict copy-only mode, a partially retained interval, out-of-range fitting, overlapping intervals and changed generated-audio bytes. The export validator decoded the output and compared source PCM outside the replacement and fitted preview PCM inside it. Output contained 96,000 samples for the two-second edited timeline.

## Live Qwen follow-up

The real 24 kHz mono Qwen preview recorded in the prior WSL acceptance was reused without running the model again. Its 24,960 samples (1.040 seconds) were applied to a 1.000-second speech interval and rendered into the four-second synthetic source. Preflight reported `matroska-lossless-timeline-v3`, copied PNG video, encoded PCM audio and an explicit 0–48,000 target-sample mapping. The completed Matroska output remained exactly 4.000 seconds with 48 kHz mono PCM audio.

No generated audio or media output is committed. This test establishes timing, mapping and sample integrity for the bounded fixture. It does not establish subjective voice quality, automatic isolated-dialogue detection, preservation of background sound inside the interval, fades, caption-text replacement, other audio formats, Linux execution or speaker diarization.

## Verification

`scripts/verify.ps1 -PublishAot` passed 45 checks against the managed CLI/MCP path and the Windows x64 NativeAOT CLI path. Builds completed with zero warnings. The suite also verifies reusable per-speaker mappings, exact-fit application, bounded time-stretch application/rejection, generated-asset fingerprint checks, explicit encoding authorization and atomic cleanup.
