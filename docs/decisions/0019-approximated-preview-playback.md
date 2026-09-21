# 0019: Approximate the timeline over one source copy instead of rendering every edit

- Date: 2026-09-21
- Status: Accepted for desktop review
- Supersedes: The playback mechanism in [0015](0015-validated-desktop-playback-proxy.md); its validated render survives as an explicit action
- Review trigger: A player that decodes more than WebM/VP9/Opus, audio-accurate preview, or a change to the validated export matrix

## Context

Decision 0015 built desktop playback by running the project through the validated export path and transcoding the result. That made playback exact, at two costs that only became clear against real material.

Every edit rendered a new video. A crop, trim, split or reorder discarded the proxy and re-ran a full validated export plus transcode before anything could play.

Worse, it inherited the export gate. A project acquired from a URL is AV1 in Matroska with Opus audio; the validated matrix is 60-second PNG/FFV1/H.264 with PCM audio. Preflight refused it with `unsupported-media`, so no proxy was ever built and the reviewer had no playback at all. The crop appeared to break playback, but an identical project with the crop removed failed identically: the audio codec was the real cause, and nothing had ever played.

The bundled player is also WebM-only. Every type in `CupriFace.Media` is `WebmFile`, `WebmTrack`, `WebmPlayer` or `WebmVideoBackend`, and `NativeDecoders.CreateVideo` takes a `WebmTrack`. Playing the untouched source is therefore not an option for most real sources either.

## Decision

Build **one** preview copy of each source — a VP9/Opus WebM transcode, scaled to at most 720 lines — keyed by the source fingerprint and cached under `.roughcut-preview`. Approximate the timeline over that copy: seek to a clip's source position, jump at clip boundaries so cuts and reordering are visible, and show a crop by scaling the picture inside a clipped frame. Editing changes which parts of the copy play, never the copy itself, so no video is rendered while editing.

Keep the validated render as **Render exact preview**, an explicit button, for confirming precisely what export would produce.

FFmpeg still decodes frames for exact stills and MCP image retrieval, and still performs the final export. What it no longer does is build a video as a side effect of an edit.

## Consequences

Playback works for any source FFmpeg can decode, including the AV1/Opus material the validated matrix rejects. The copy is built once and reused across revisions and sessions: the acquired 18.9-second AV1 source took 1.6 seconds to prepare and 0.2 seconds to reuse.

Playback is an approximation and is labelled as one. Boundary jumps land on the decoder's nearest frame rather than exactly on the cut, audio is not gapless across a jump, the copy is re-encoded so it is not frame-accurate evidence of output, and crop is not applied to the moving picture because the preview box cannot take the crop aspect ratio in this layout engine. Anything that must be exact goes through export, or through the explicit render.

Approximation needs a timeline built from exactly one video source; image holds and multi-source timelines keep the exact-frame poster. The preview cache is disposable, ignored by source control, and bounded at 512 MiB per source.

## Evidence

See the [desktop guide](../desktop.md) and the executable checks in `RoughCut.Tests`, which cover locating an output time in source, jumping at a boundary, leaving contiguous clips alone, stopping at the end, and refusing a multi-source timeline.
