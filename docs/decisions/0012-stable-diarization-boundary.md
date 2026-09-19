# 0012: Stable provider-neutral diarization boundary

- Date: 2026-09-19
- Status: Accepted for bounded CLI/MCP foundation
- Review: When adopting a concrete diarization runtime, changing transcript alignment, or adding speaker enrollment/identity

## Context

RoughCut had manually correctable speaker records and Qwen replacement, but no safe way to accept diarization output. Provider cluster numbers are local classifications rather than real-world identity, transcript segments may be uncovered or contain concurrent speakers, and rerunning analysis must not erase reviewed corrections or recreate merged speakers.

The official [sherpa-onnx diarization documentation](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/index.html) and [C# example](https://github.com/k2-fsa/sherpa-onnx/blob/master/dotnet-examples/offline-speaker-diarization/Program.cs) expose offline diarization using a Pyannote segmentation model, speaker embeddings and clustering. Its current [NuGet package](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx/1.13.8) is 1.13.8 and supplies native Windows/Linux libraries. No model was downloaded or executed in this slice, so runtime quality, model licensing, cancellation and packaging remain unaccepted.

## Decision

Accept bounded provider/model/source-time turns through a provider-neutral contract. Map each provider speaker key deterministically to a persistent project speaker ID and retain the explicit key-to-ID mapping in project provenance. Reuse it for repeated submissions from the same asset/provider/model. When a reviewer merges speakers, redirect all mapped keys to the retained ID.

Apply diarization only to speech from the selected source. Preserve `corrected` assignments. For other transcript segments, assign the speaker with the greatest covered duration, record all speakers that are actually concurrent as overlap, and leave uncovered segments unknown. Reject invalid source ranges, more than 10,000 turns, more than 1,000 keys or more than eight concurrent speakers. Store the source hash, provider, model, canonical submission hash and revision.

Expose the same revision-safe, source-fingerprint-checked operation through CLI and MCP. Keep model execution behind `ISpeakerDiarizer` so a native runtime remains separate from project rules and Qwen synthesis.

## Consequences

Agents can provide diarization through MCP without editing project JSON directly, and later local runtimes can use the same application boundary. Speaker IDs remain stable for repeated analysis and manual decisions win over model output. Cluster keys do not identify a person across unrelated sources or models.

The next runtime gate is sherpa-onnx 1.13.8 on labelled multi-speaker fixtures. Adoption requires measured assignment/overlap quality, source decoding, cancellation behavior, native/model packaging and license review on Windows, followed by Linux verification.

## Evidence

See [2026-09-19 diarization-boundary evidence](../evidence/2026-09-19-diarization-boundary.md).
