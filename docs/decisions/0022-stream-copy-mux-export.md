# 0022: Stream-copy mux export with caller-chosen anchors

- Date: 2026-09-27
- Status: Accepted for the fast-export slice
- Sits beside: [0020](0020-delivery-encode-export.md), which re-encodes the same timeline when the cut must land exactly

## Context

RoughCut had two export paths and neither produced a file quickly. The strict exporter refuses ordinary acquired media outright, and delivery ([0020](0020-delivery-encode-export.md)) decodes and re-encodes everything, which takes minutes and produces a generation loss nobody asked for. A person cutting the talking out of a downloaded music video wants the original packets back in a smaller file, in seconds.

Copying packets cannot honour an arbitrary cut. Video decodes from a keyframe, so a copy that begins between keyframes writes a file whose picture starts late — and the muxer reports nothing. Measured on the test source, a copy asked to begin at 2.839 s produced a silent hole before the first picture. Asking FFmpeg to seek on the input instead overshot: a request for 5 s produced 9.207 s, exactly one 4.207 s keyframe interval late.

So the honest options are to move the cut to where the source allows, or to re-encode. Which one is right is editorial, not technical: it depends on whether the boundary or the pixels matter. The caller — an agent or a person — is the only one who knows.

## Decision

Add a third export path that copies packets and never encodes, and give the caller what it needs to decide whether to use it.

**`MuxExporter` copies, and says what it moved.** Only the *start* of each segment snaps to a keyframe anchor; the end stays exactly where it was asked for, because a copy can stop anywhere. `MuxSegment` records `RequestedIn`/`RequestedOut` beside the anchors actually used and the signed offset between them, so the plan states the error rather than implying there is none. Bounds are 200 segments and 8 GiB.

Every output is checked to begin on a keyframe before it is published, because the failure above is silent and therefore has to be tested for rather than reasoned about.

**One pass, not one per segment.** Cutting each segment to its own file and concatenating them inflated the result — 5.406 s for a 5.0 s cut, 7.406 s for 7.0 s — because each part was rounded outward and the errors accumulated. The exporter instead writes one `ffconcat` list of `inpoint`/`outpoint` pairs over the same input and runs FFmpeg once, so the boundaries are resolved by the demuxer in a single timeline.

**Warnings are read, not silenced.** Where two copied segments meet, audio packets are whole and do not align with the picture cut, so the muxer moves some of them forward and says so. Those lines are counted into `MuxReport.JoinAdjustments`; any *other* warning still fails the export. Suppressing the class would have hidden everything else in it.

**`CutPointReader` shows the choice before it is made.** Given a time, it reports the keyframe anchors behind and ahead with their offsets, whether an audio packet starts within half a packet of each, and the source's own median keyframe interval. It reads a bounded window of packets without decoding, and widens the window from that measured interval when the first attempt finds nothing: 1.2 s, against over 120 s for a full index of the same file. `AudioAligned` is **null**, not false, outside the sampled audio span, because "not known here" is not "not aligned".

`preflight-mux`, `cut-points`, `roughcut_preflight_mux`, `roughcut_start_mux` and `roughcut_list_cut_points` expose all of this, so a headless caller can inspect the anchors around the time it cares about, choose them, and then copy — or decide the cut must be exact and use delivery instead.

## Consequences

- A cut-down of an acquired video is available in seconds, with the source's own packets, at the cost of boundaries that sit on the source's keyframes.
- Three export paths now exist with three different claims: strict proves what it copied, delivery renders the edit faithfully by re-encoding, mux copies quickly and states where the cuts actually landed. A caller must choose, and the plan in each case says what is being claimed.
- Nothing about a mux output is evidence about frame-exact editorial intent. Where the boundary matters more than the pixels, delivery is the answer, and preflight says so rather than approximating.
- Joins can move audio packet timestamps. The packets are untouched; only their positions change, and the count is published.

## Evidence

`docs/evidence/2026-09-27-mux-and-measurement.md`. The suite covers anchor resolution, the keyframe check on the published output, the single-pass duration, and the refusal paths. The real-source run is recorded in the same note: a 13-minute 1440×1080 download cut to its music and copied out, with the plan's offsets and join adjustments.

## Review trigger

Before allowing a mux export to snap a segment *end*, before adding a container that cannot carry the copied streams, or if a join adjustment is ever observed to change a packet rather than its position.
