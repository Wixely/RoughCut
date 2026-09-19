# Desktop background media preparation evidence

- Date: 2026-09-19
- Platform: Windows, .NET SDK 10.0.300
- Scope: RC-06 frame selection, revision writes and playback-proxy preparation

The desktop review command now opens the CupriFace window after reading the project instead of waiting for an exact frame and a synchronized proxy. Initial frame extraction, subsequent transcript/evidence/clip selection and proxy generation execute outside the click/event path. The status line reports frame loading, revision saving and proxy preparation.

Selection work is serialized and cancellable. Starting a newer selection cancels the older request; decoded frame values are committed together only after cancellation and revision checks pass. Revision-changing commands use the same serial gate and reject a concurrent write. Playback preparation has a separate cancellation generation so selection remains usable while the proxy is generated, and a proxy is committed only when its captured project revision is still current.

Executed:

```powershell
.\scripts\verify.ps1
```

Observed: 54 checks passed and none failed. The added deterministic check proves that starting work returns promptly, a replacement selection suppresses the stale completion, and a second revision command is rejected while the first is active. Existing desktop checks still render the 1280×800 review surface, persist crop undo/redo and decode the synchronized proxy with bounded dummy-device A/V drift.

Limitations: no responsiveness timing was measured in a physical window, and background preparation does not reduce FFmpeg cost. The proxy remains bounded to 60 seconds and 128 MiB and is loaded completely by the native player. Direct crop dragging, physical audio/window acceptance and Linux validation remain.
