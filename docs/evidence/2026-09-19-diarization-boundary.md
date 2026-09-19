# Stable diarization-boundary evidence

- Date: 2026-09-19
- Scope: Provider-neutral diarization persistence and correction integration on Windows
- Review trigger: Backend adoption, alignment policy, stable-ID rules or speaker correction changes

## Executed checks

The labelled contract fixture contains four transcript segments: one dominated by a single speaker, one with two concurrent speakers, one manually corrected segment and one uncovered segment. The planner inferred the first two, marked overlap only for the concurrent interval, preserved the reviewed assignment and left the uncovered interval unknown.

Repeating the same asset/provider/model submission reused both project speaker IDs without adding speakers. A reviewed merge redirected both provider keys to the retained project ID, and a later submission did not recreate the removed speaker. Invalid out-of-source turns were rejected.

CLI execution read a bounded JSON submission, verified the media fingerprint, persisted two stable inferred assignments and returned the saved provenance. The official MCP client listed `roughcut_save_diarization`, called it against a workspace project and received the two persisted mappings. Revision conflicts, project validation and atomic saves use the existing shared operations.

`scripts/verify.ps1 -PublishAot` passed 47 checks against both the managed CLI/MCP path and the Windows x64 NativeAOT CLI path, with zero build warnings. The core and main CLI add no native diarization dependency.

## Candidate evaluation

Official [sherpa-onnx documentation](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/index.html) and its [C# example](https://github.com/k2-fsa/sherpa-onnx/blob/master/dotnet-examples/offline-speaker-diarization/Program.cs) describe an offline API combining Pyannote segmentation, an embedding extractor and clustering. NuGet currently publishes [`org.k2fsa.sherpa.onnx` 1.13.8](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx/1.13.8) with prebuilt native libraries. This makes it the leading local Windows/Linux candidate, but source inspection is not runtime acceptance.

No model, native package or external audio was downloaded or executed. Representative diarization error, overlap quality, automatic speaker-count tuning, processing time, memory, cancellation, model licensing, NativeAOT behavior and Linux packaging remain unverified. Provider speaker keys are stable only within the persisted asset/provider/model mapping and are not claims of real-world identity.
