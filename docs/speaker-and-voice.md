# Speaker correction and voice preview

- Updated: 2026-09-19
- Review: Before adopting a diarization or Qwen runtime
- Owner: Implementation agent

RoughCut now keeps stable speaker IDs separate from display labels. A revision-checked speaker edit batch can add or rename speakers, assign one or more speakers to transcript segments, or merge labels. Adding a speaker and assigning selected segments provides the split workflow. Every change records its action, affected segments, before/after IDs or labels, reason and project revision.

The bounded voice workflow is deliberately reversible:

1. Confirm a single, non-overlapping speaker assignment.
2. Create a `qwen-tts` mapping and replacement request with `voice-plan` or `roughcut_plan_voice_replacement`.
3. Generate audio in a separately chosen Qwen runtime, then import mono/stereo 16-bit PCM WAVE at 8–96 kHz with `voice-preview-import` or `roughcut_import_voice_preview`.
4. Retrieve the exact bytes through `voice-preview` or the MCP `audio/wav` content block from `roughcut_get_voice_preview`.
5. Set the preview to `applied` after review, or `reverted` while retaining the source and generated preview.

The project records provider, model, runtime, voice, language, text hash, audio hash, requested duration, actual duration and fit policy. WAVE data is capped at 16 MiB, parsed without executing supplied content, stored at `assets/audio/<sha256>.wav`, and checked again when read. Only exact-duration replacement is accepted for application. Overlapping speech, uncertain speaker assignments and any background policy other than `require-isolated-dialogue` are rejected. RoughCut does not yet verify that a source really contains isolated dialogue, mix generated audio into export, separate background sound, or synthesize directly.

Applied replacements make export return `unsupported-voice-replacement`; requested, previewed and reverted replacements do not change media output. This keeps review useful without implying that export rendered a replacement.

The official [Qwen3-TTS repository](https://github.com/QwenLM/Qwen3-TTS) currently documents 0.6B and 1.7B CustomVoice/Base models plus a 1.7B VoiceDesign model, ten languages, Python package setup and CUDA examples. The [1.7B Base model card](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base) reports Apache-2.0 and about 4.54 GB of model files. A read-only local candidate wrapper was also reviewed at commit `404a39b1eaf9f89c6a05182721808830e6e56805`; it offers an OpenAI-compatible speech endpoint around `faster-qwen3-tts>=0.2.6`, declares Python 3.10+ and CUDA-only operation, and has not been executed or adopted by RoughCut.

No Python environment, model, server, endpoint or cloud service was installed, configured or contacted. Choosing a runtime still needs GPU/resource measurements, voice-quality examples, Windows/Linux execution, cancellation behavior and a disclosure decision for any non-local provider.

## CLI examples

```powershell
dotnet run --project src/RoughCut.Cli -- speaker-edit project.json examples/speaker-edits.json 1
dotnet run --project src/RoughCut.Cli -- voice-plan project.json examples/voice-plan.json 2
dotnet run --project src/RoughCut.Cli -- voice-preview-import project.json replacement-1 generated.wav 3 faster-qwen3-tts@0.2.6
dotnet run --project src/RoughCut.Cli -- voice-preview project.json 4 replacement-1 reviewed.wav
dotnet run --project src/RoughCut.Cli -- voice-state project.json 4 replacement-1 applied
dotnet run --project src/RoughCut.Cli -- voice-state project.json 5 replacement-1 reverted
```

Next owner/action: **Implementation agent: add a cancellable local Qwen provider adapter after runtime approval and evaluate diarization against labelled fixtures; Wixely: provide representative multi-speaker and replacement examples.**
