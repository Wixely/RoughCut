# Speaker correction and voice preview

- Updated: 2026-09-19
- Review: When the Qwen service contract/runtime, duration policy, diarization or rendering changes
- Owner: Implementation agent

RoughCut now keeps stable speaker IDs separate from display labels. A revision-checked speaker edit batch can add or rename speakers, assign one or more speakers to transcript segments, or merge labels. Adding a speaker and assigning selected segments provides the split workflow. Every change records its action, affected segments, before/after IDs or labels, reason and project revision.

The bounded voice workflow is deliberately reversible:

1. Confirm a single, non-overlapping speaker assignment.
2. Create a `qwen-tts` mapping and replacement request with `voice-plan` or `roughcut_plan_voice_replacement`.
3. Generate audio through the configured loopback service with `voice-synthesize` or `roughcut_synthesize_voice`. Importing an externally generated mono/stereo 16-bit PCM WAVE at 8–96 kHz remains available through `voice-preview-import` or `roughcut_import_voice_preview`.
4. Retrieve the exact bytes through `voice-preview` or the MCP `audio/wav` content block from `roughcut_get_voice_preview`.
5. Set the preview to `applied` after review, or `reverted` while retaining the source and generated preview.

The project records provider, model, runtime, voice, language, text hash, audio hash, requested duration, actual duration and fit policy. WAVE data is capped at 16 MiB, parsed without executing supplied content, stored at `assets/audio/<sha256>.wav`, and checked again when read. Only exact-duration replacement is accepted for application. Overlapping speech, uncertain speaker assignments and any background policy other than `require-isolated-dialogue` are rejected. RoughCut does not yet verify that a source really contains isolated dialogue, mix generated audio into export, separate background sound, or synthesize directly.

Applied replacements make export return `unsupported-voice-replacement`; requested, previewed and reverted replacements do not change media output. This keeps review useful without implying that export rendered a replacement.

The provider accepts only absolute loopback HTTP(S) endpoints. Before synthesis it checks `/api/status` for the exact configured model, loaded state and language, then posts the text and voice to `/v1/audio/speech`. It accepts at most 16 MiB of PCM WAVE data, honors caller cancellation and a 10-minute default timeout, and records the service version. `ROUGHCUT_QWEN_ENDPOINT` is required; `ROUGHCUT_QWEN_API_KEY` and `ROUGHCUT_QWEN_TIMEOUT_SECONDS` are optional. The endpoint is process configuration and is never persisted in a project.

The accepted local feasibility deployment runs the reviewed `faster-qwen-tts-aio` wrapper at commit `404a39b1eaf9f89c6a05182721808830e6e56805` in WSL2 with `faster-qwen3-tts` 0.4.0, PyTorch/torchaudio 2.11.0+cu128 and Transformers 5.15.1. Transformers 5.17.0 failed during tokenizer model initialization and must not be used for this tested configuration. Only the `Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice` model was loaded, with the service bound to `127.0.0.1`. See the [live evidence](evidence/2026-09-19-qwen-wsl.md).

This establishes a local provider boundary, not a packaged RoughCut dependency. The external WSL environment and model cache remain outside the repository. Voice quality, representative content, server-side interruption of an inference already running, Windows-native service operation and general Linux support remain unverified. Cloud endpoints are rejected.

## CLI examples

```powershell
dotnet run --project src/RoughCut.Cli -- speaker-edit project.json examples/speaker-edits.json 1
dotnet run --project src/RoughCut.Cli -- voice-plan project.json examples/voice-plan.json 2
$env:ROUGHCUT_QWEN_ENDPOINT = "http://127.0.0.1:8080/"
dotnet run --project src/RoughCut.Cli -- voice-synthesize project.json 3 replacement-1
dotnet run --project src/RoughCut.Cli -- voice-preview-import project.json replacement-1 generated.wav 3 faster-qwen3-tts@0.2.6
dotnet run --project src/RoughCut.Cli -- voice-preview project.json 4 replacement-1 reviewed.wav
dotnet run --project src/RoughCut.Cli -- voice-state project.json 4 replacement-1 applied
dotnet run --project src/RoughCut.Cli -- voice-state project.json 5 replacement-1 reverted
```

Next owner/action: **Implementation agent: add explicit duration fitting and replacement rendering, then evaluate diarization against labelled fixtures; Wixely: provide representative multi-speaker and replacement examples.**
