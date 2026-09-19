# Local sherpa-onnx diarization evidence

- Date: 2026-09-19
- Scope: Windows x64 local runtime, dedicated CLI packaging and stable transcript assignment
- Review trigger: dependency/model change, Linux claim, or representative quality acceptance

## Executed configuration

- .NET SDK 10.0.300 on Windows x64
- `org.k2fsa.sherpa.onnx` 1.13.8, package license expression Apache-2.0
- Pyannote segmentation 3.0 int8 model from the official sherpa-onnx release archive; archive SHA-256 `24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488`; included license MIT
- 3D-Speaker ERes2Net embedding model from the official sherpa-onnx model release; SHA-256 `1a331345f04805badbb495c775a6ddffccdd1a732567d5ec8b3d5749e3c7a5e4b`
- Official `1-two-speakers-en.wav` fixture; 16 seconds; SHA-256 `f1c877dc01595e28be7147bf2fe38e5268147a868bf3fdb5c37b97f5940e21f3`
- Fixed speaker count: 2

The models and audio remain beneath ignored `artifacts/sherpa-diarization/`; no model or media binary is committed.

## Results

The Debug provider and a framework-dependent Windows x64 publish both loaded the native runtime and completed inference. The initial published `roughcut-diarization.exe` run took 1.878 seconds wall time, including process startup and FFmpeg decode. After process isolation, the Debug CLI completed the same fixture in 1.534 seconds and the republished Windows x64 CLI completed it in 1.694 seconds. All runs created two stable project speakers. Each two-second transcript interval from 0 through 8 seconds mapped to the first speaker, and each interval from 8 through 16 seconds mapped to the second. The persisted model provenance was `pyannote-segmentation-3.0:d582f4b4c6b4+3dspeaker-eres2net:1a331345f048@1.13.8`.

The provider validates source duration, bounds decoded PCM and native turns, fingerprints models before and after inference, and stores no local model path. CLI and MCP execute the native provider in a child process. An executable contract fixture waits inside that child; cancellation kills it, removes the temporary project JSON and leaves the saved project at its prior revision. The MCP protocol suite discovers `roughcut_diarize_local` and verifies actionable failure without model configuration or project mutation. Full managed and main-CLI NativeAOT verification are recorded in the handoff; the main CLI does not reference sherpa-onnx.

## Limits

This official sample is a useful wiring and gross speaker-change fixture, not a representative diarization benchmark. Speaker error rate, overlap handling, similar voices, noisy material and automatic speaker-count tuning remain unmeasured. Linux execution and NativeAOT for the diarization CLI/MCP host are unverified.
