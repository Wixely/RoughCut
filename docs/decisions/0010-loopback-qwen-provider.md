# 0010: Loopback Qwen provider through WSL

- Date: 2026-09-19
- Status: Accepted for bounded local MVP
- Review: When the service API, model/runtime, duration policy, packaging or remote-provider policy changes
- Supersedes: The runtime deferral in [0009](0009-reversible-speaker-and-voice-boundary.md); its reversible preview and safety rules remain active

## Context

The existing voice workflow could import a generated WAVE but could not invoke Qwen. The user approved WSL for the Python/CUDA runtime. RoughCut still needs a headless provider path that does not embed Python in the managed product or allow an endpoint setting to disclose replacement text to a remote service.

## Decision

Add a provider-neutral synthesis interface and a `faster-qwen-tts-aio` HTTP adapter. Accept only absolute loopback HTTP(S) endpoints. Query service status before every request and require the exact mapped model to be loaded and the configured language to match. Request WAVE, cap the response at 16 MiB, validate its PCM structure, preserve cancellation and timeout behavior, and pass the bytes through the existing content-addressed preview/provenance workflow.

Expose synthesis through CLI and stdio MCP. Configuration stays in process environment variables and is not written to project JSON. Preserve manual WAVE import as an interoperability and recovery path.

Accept the tested WSL2 deployment as feasibility evidence: CustomVoice 1.7B through the reviewed wrapper, `faster-qwen3-tts` 0.4.0, PyTorch/torchaudio 2.11.0+cu128 and Transformers 5.15.1. The WSL environment and model cache are external operational dependencies, not repository dependencies. Transformers 5.17.0 is excluded from the tested configuration because model initialization failed.

## Consequences

CLI and MCP can now create real local Qwen previews with model/runtime/hash/duration provenance. They cannot send text to a LAN or cloud endpoint. A caller can cancel waiting for a request, but the external service has no proven mechanism to stop GPU inference already running.

Exact-duration application remains strict. The live sample produced 1.040 seconds for a 1.000-second segment and was correctly retained as a preview rather than applied. Duration fitting, audio replacement rendering, diarization, voice-quality acceptance and distributable service packaging remain separate work.

## Evidence

See [2026-09-19 Qwen WSL evidence](../evidence/2026-09-19-qwen-wsl.md).
