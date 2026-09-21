# 0015: Play a validated revision-keyed WebM proxy on desktop

- Date: 2026-09-19
- Status: Accepted for bounded Windows desktop playback; its playback mechanism is superseded by [0019](0019-approximated-preview-playback.md)
- Review: Export matrix expansion, proxy format change, physical-device acceptance or Linux acceptance

## Context

Decision 0014 selected CupriFace for the initial review shell but withheld any live playback claim because the inspected source checkout lacked native codec artifacts. The official CupriFace.Media 0.26.1 release package includes VP8/VP9 and Opus decoder libraries for Windows, Linux and macOS desktop RIDs. RoughCut still needs playback to represent cuts, reordering, crops, inserted images and applied voice replacements instead of playing the unchanged source file.

## Decision

Generate desktop playback from the same bounded, decoded-content-validated timeline export used by final output. Transcode that validated Matroska artifact to a VP9/Opus WebM proxy, verify codec, dimensions, duration and a 128 MiB size limit, then publish it atomically under `.roughcut-preview`. Key the filename by the exact project JSON hash and revision. Unsupported export plans keep the exact-frame poster and report the reason.

Use CupriFace.Media's `WebmVideoBackend`, native decoders and SDL audio sink behind a RoughCut desktop adapter. Transcript and evidence selection seek the player to the exact resolved timeline position. Keep this cache outside portable project JSON and out of source control.

## Consequences

Playback reflects the accepted project timeline and can reuse a verified proxy for an unchanged revision. The implementation inherits the current export gate of one active video/audio source, up to 32 clips and 60 seconds. Initial preparation blocks window opening, reload/frame preparation can block the UI thread, and the decoder reads the complete bounded proxy into memory. Crop manipulation, physical audio-device/window acceptance and Linux execution remain.

The main CLI NativeAOT boundary remains independent of CupriFace.Media. The desktop continues as a framework-dependent publish until a separate NativeAOT experiment passes.

## Evidence

See [2026-09-19 desktop playback evidence](../evidence/2026-09-19-desktop-playback.md).
