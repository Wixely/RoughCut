# Speaker and voice foundation evidence

- Date: 2026-09-19
- Platform: Windows x64
- Scope: RC-01 speaker/synthesis contracts and bounded RC-09 preview foundation
- Review trigger: Diarization/Qwen runtime adoption, duration fitting, background handling or replacement rendering

## Command

```powershell
.\scripts\verify.ps1 -PublishAot
dotnet format RoughCut.slnx --verify-no-changes --no-restore
```

## Results

- The managed suite passed 42 checks.
- The Windows x64 NativeAOT CLI suite passed the same 42 checks while using the normal .NET MCP host.
- Speaker add/rename/assign/merge contracts persist revisioned correction history. The executable workflow tested add plus assignment and rename through shared operations, CLI and MCP.
- Voice planning requires one corrected, non-overlapping speaker and the explicit isolated-dialogue/exact-duration policies.
- A synthetic one-second mono 8 kHz PCM16 WAVE round-tripped through content-addressed storage, CLI output and an MCP `audio/wav` block. Hash, model/runtime, voice/language, text and requested/actual duration provenance survived save/reopen.
- Apply, revert, stale revision, malformed WAVE and duration-mismatch paths returned the intended results.
- Export still returns `unsupported-voice-replacement` for an applied replacement; a preview or reverted record does not alter media output.
- Formatting verification passed. The 27 VS Code launch profiles and both JSON examples parse successfully.

## Feasibility observations

The official Qwen3-TTS project documents 0.6B/1.7B model variants, Python package installation and CUDA-oriented examples. Its 1.7B Base model card reports Apache-2.0 and roughly 4.54 GB of files. A read-only local wrapper candidate at commit `404a39b1eaf9f89c6a05182721808830e6e56805` exposes an OpenAI-compatible speech endpoint and declares Python 3.10+, `faster-qwen3-tts>=0.2.6` and CUDA-only execution.

No Python environment, Qwen model, inference server, endpoint, voice quality, GPU use, Linux behavior, live cancellation, diarization or replacement rendering was executed or accepted. These results validate the provider boundary and reversible project workflow only.

Next owner/action: **Implementation agent: propose and, after runtime approval, test a cancellable local Qwen adapter; Wixely: provide representative multi-speaker and replacement examples.**
