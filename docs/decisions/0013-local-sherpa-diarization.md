# 0013: Use sherpa-onnx for bounded local diarization

- Date: 2026-09-19
- Status: Accepted for bounded Windows MVP
- Review: sherpa/model upgrade, Linux acceptance, or representative quality testing

## Context

RoughCut already preserves stable speaker IDs and reviewed corrections, but the provider-neutral boundary in [0012](0012-stable-diarization-boundary.md) did not execute a model. Whisper remains the timed transcription provider and does not supply the speaker clustering required by this product.

The official [sherpa-onnx speaker-diarization API](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/index.html) combines Pyannote segmentation, speaker embeddings and clustering. The [`org.k2fsa.sherpa.onnx` 1.13.8 package](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx/1.13.8) declares Apache-2.0 and provides Windows and Linux native runtime packages. The evaluated Pyannote segmentation archive carries MIT terms; the official [3D-Speaker repository](https://github.com/modelscope/3D-Speaker) carries Apache-2.0.

## Decision

Use sherpa-onnx 1.13.8 as the optional local diarization provider. Keep it in a separate provider assembly and `roughcut-diarization` CLI so the main CLI remains independent of this native dependency and retains its established NativeAOT gate. Decode one selected source audio stream through FFmpeg to bounded 16 kHz mono PCM, run offline segmentation and embedding clustering, and submit the resulting source-time turns through the stable assignment boundary from 0012.

Require explicit absolute model paths. Permit a known speaker count from 1 through 64 or zero for threshold clustering. Limit this slice to ten minutes and 10,000 returned turns. Fingerprint both model files before and after inference and persist only provider/model versions and hash prefixes, never machine paths. Expose the same operation through MCP host environment configuration.

## Consequences

Whisper continues to produce transcript text and timestamps. Sherpa assigns speaker clusters to those segments; it does not replace transcription or identify real people. The official two-speaker fixture produced two stable project speakers and split the eight test transcript intervals at the expected speaker change on Windows x64.

The CLI and MCP host execute FFmpeg decoding and native inference in a dedicated worker process. Cancellation or timeout kills the entire worker process tree, deletes the bounded temporary project copy and prevents revision persistence. Representative overlap/error measurement and Linux execution remain release work. Models remain external ignored assets and are not redistributed by this repository.

## Evidence

See [2026-09-19 Sherpa diarization evidence](../evidence/2026-09-19-sherpa-diarization.md).
