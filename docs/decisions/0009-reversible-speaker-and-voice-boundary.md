# 0009: Reversible speaker and voice boundary

- Date: 2026-09-19
- Status: Accepted for bounded CLI/MCP foundation
- Review: When adopting a diarization or Qwen runtime, adding duration fitting, or rendering replacement audio

## Context

The MVP requires stable, correctable speaker labels and Qwen TTS replacement without losing original speech. Runtime selection remains material because the official implementation uses a Python model stack and CUDA-oriented examples, while the reviewed local OpenAI-compatible wrapper is Python/CUDA-only. Neither has Windows/Linux evidence in RoughCut.

## Decision

Persist atomic speaker correction history and explicit Qwen voice mappings independently of any diarization provider. Treat generated speech as untrusted imported PCM WAVE with complete provider/model/runtime, text/audio hash and requested/actual duration provenance. Use `requested`, `preview`, `applied` and `reverted` states. Permit application only for exact-duration, corrected, non-overlapping speech under an isolated-dialogue policy. Return preview bytes directly through MCP. Continue rejecting applied replacements during export until audio rendering and background preservation are proven.

Do not adopt or configure a Python/CUDA runtime in this slice. The local OpenAI-compatible wrapper remains an interoperability candidate rather than a dependency.

## Consequences

Agents and CLI users can correct speaker identity, bring in externally generated Qwen audio, inspect it and reverse the choice without changing source media. Projects record enough information to invalidate or regenerate previews later. Direct synthesis, automatic diarization, duration fitting, background separation, replacement rendering and runtime cancellation remain work for RC-09.

## Evidence

Managed tests exercise revisioned speaker correction, exact WAVE validation, content-addressed storage, provenance, preview, apply, revert and stale revisions. CLI and the official MCP client exercise the same workflow; MCP decodes the returned `audio/wav` block. The full Windows suite and NativeAOT CLI verification are recorded in the handoff. No Qwen inference or speaker-quality result is claimed.
