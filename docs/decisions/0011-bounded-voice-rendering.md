# 0011: Bounded duration fitting and voice rendering

- Date: 2026-09-19
- Status: Accepted for bounded local MVP
- Review: When fit limits, background handling, fades, caption rewriting or the export matrix changes
- Supersedes: The exact-only application and export rejection in [0009](0009-reversible-speaker-and-voice-boundary.md) and [0010](0010-loopback-qwen-provider.md); their provenance, reversibility and loopback rules remain active

## Context

Qwen generated a 1.040-second preview for a 1.000-second speech interval. RoughCut preserved it but could neither apply nor export it. Rendering must keep source timing stable, preserve source audio outside the selected interval, remain reversible, and avoid implying that mixed background sound can be separated from dialogue.

## Decision

Keep `exact` fitting and add an explicit `time-stretch` policy bounded to an input/output tempo ratio from 0.8 through 1.25. Reject previews outside that range. Resample an applied preview to the source's 48 kHz mono/stereo PCM layout, apply the recorded tempo adjustment, and trim or pad only after stretching to produce the exact target sample count. Preserve the original preview asset and provenance.

Render only corrected, non-overlapping speech marked `require-isolated-dialogue`. Each applied interval must be fully retained exactly once within one video timeline clip; partial cuts, duplicated uses and overlaps are unsupported. Replace source PCM only within that interval and preserve every source sample outside it. Do not add a crossfade or attempt source separation.

Voice rendering always encodes the audio stream and therefore requires an encoding-permitted project mode plus explicit export authorization. Independently copyable video may remain copied. Record each replacement's source interval, target sample interval, input format and fit policy in export plan policy `matroska-lossless-timeline-v3`. Recheck generated-audio hashes before rendering and publication, and compare the final replacement samples against the fitted PCM during validation.

## Consequences

CLI and MCP users can plan, synthesize/import, preview, apply, preflight and export bounded Qwen replacements without shifting later media. One speaker mapping can be reused for multiple replacement requests. Revert remains a project-state change and sources/previews remain untouched.

The isolated-dialogue policy is a reviewed assertion; RoughCut does not detect or preserve background sound inside the replaced interval. Existing captions are retimed but their text is not rewritten to replacement text. Voice quality, fades, wider tempo changes, partial/repeated intervals, overlapping speakers, compressed delivery formats and diarization remain future work.

## Evidence

See [2026-09-19 voice-rendering evidence](../evidence/2026-09-19-voice-rendering.md).
