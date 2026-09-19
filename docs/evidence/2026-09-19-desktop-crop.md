# Desktop crop evidence — 2026-09-19

## Scope

The desktop crop editor uses the existing project `Crop` contract and transactional `crop` edit operation. It displays an uncropped source PNG at the selected clip time, scales an orange crop rectangle proportionally over that source frame, accepts integer X/Y/width/height values and offers an explicit Full frame reset. Apply, reset, undo and redo all use saved project revisions; crop changes invalidate the playback proxy and refresh the exact rendered timeline frame.

## Executed results

On Windows with .NET SDK 10.0.300 and the repository's recorded FFmpeg/FFprobe build:

```powershell
.\scripts\verify.ps1 -PublishAot
```

The managed path passed 53 checks and the repeated path using the published Windows x64 NativeAOT main CLI passed 53 checks. The new crop check selected a real synthetic source frame, applied an inset crop, undid it and redid it. The saved project reached revision 4 with the requested crop, the uncropped source image remained available, the timeline preview reported the crop, the playback proxy was invalidated, and undo remained available while redo was exhausted.

Headless 1280 by 800 snapshots were visually inspected before and after the edit. The full-frame editor showed the complete 160 by 96 source bounded by the crop rectangle. After applying `(8, 4, 144, 88)`, the main preview showed the cropped output while the source thumbnail retained the complete image and placed the orange rectangle inside its edges. Controls, speakers, transcript, timeline and evidence stayed within their panels.

## Limits

The rectangle is a visual reflection of the numeric values; it is not draggable. Preparing source/timeline frames and rebuilding the playback proxy still blocks the UI action. An interactive window/debugger session, pointer crop manipulation, physical audio and Linux execution remain unverified.
