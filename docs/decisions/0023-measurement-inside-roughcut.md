# 0023: Measurement belongs inside RoughCut

- Date: 2026-09-27
- Status: Accepted as a boundary for every future analysis feature
- Constrains: [0008](0008-evidence-backed-analysis.md), which already required evidence before proposals

## Context

Asked to cut the non-music sections out of a downloaded music video, RoughCut could acquire it, cut it and export it — but it could not find them. So the finding happened outside: an ad-hoc FFmpeg loudness probe, in a script beside the project, reaching its own conclusions about where the song started.

That is not a product. Nothing about the measurement was in the project file, nothing could be replayed, and nothing stopped the script being wrong — which it was, twice. The instruction that settled it: *whatever way you measure it, you need to use RoughCut to do it; if it doesn't support it it needs it as a feature*.

There was also a substantive reason the ad-hoc measurement kept failing. Level alone cannot tell a loud orchestral score from a loud pop record, and the test source opens with the former. Every threshold on loudness put the cut in the wrong place.

## Decision

Any measurement a caller needs in order to decide an edit is a RoughCut feature, exposed through the CLI and MCP, returning numbers the caller interprets. RoughCut measures; it does not conclude.

`AudioProfiler` is the first of these. One decode pass at 8 kHz mono, bounded to 4000 windows, reporting per window: level and peak in dBFS, the level below a configurable band split (200 Hz by default), that band's **share** of total energy, and the fraction of the window below a −60 dBFS silence floor. The band split is applied in the same pass, by a second-order Butterworth low-pass written here rather than a filter dependency, so finding sections costs one decode and not two.

The share is what level could not provide. On the test source, drums and bass enter at 253 s and the share moves from 0.008 to 0.435 while the level barely changes — the section boundary is plainly visible in a number that loudness does not contain.

Two properties are deliberate:

- **The records carry no verdicts.** `AudioWindow` has no `isMusic` field and never will. Where the sections are is the caller's judgement, exactly as choosing a rendition and choosing a cut anchor are.
- **A measurement that means little says so.** The low-pass carries energy across a transition, so a near-silent window can read a high share — one read 2.015 before the share was clamped to [0,1]. `LowBandDb` is published beside the share, and the contract says to read them together, instead of a single number that is quietly meaningless at the edges.

The same rule produced `CutPointReader` ([0022](0022-stream-copy-mux-export.md)) and `roughcut_list_source_formats`: where a caller was previously expected to guess or to probe the media itself, RoughCut measures and reports.

## Consequences

- Deciding where to cut is now possible headlessly, from RoughCut's own numbers, and the numbers are the same ones a person would see.
- Combining the band profile with transcript timings classified all 152 segments of the test source into spoken and sung without a second model, because the two questions — were there words, and was there a band playing — are measured separately and joined by the caller.
- RoughCut will accumulate measurement tools rather than one analyser. That is the intent: each is cheap, bounded and independently checkable.
- No tool of this kind may return a conclusion. A future feature that wants to say "this is music" belongs in the analysis/proposal path of [0008](0008-evidence-backed-analysis.md), where a decision is recorded with its evidence and can be reviewed and reversed.

## Evidence

`docs/evidence/2026-09-27-mux-and-measurement.md`, which records the 253 s transition as measured, the clamped ringing case, and the spoken/sung classification of the real source.

## Review trigger

Before any measurement tool returns a classification, or before a second band-split implementation is added rather than this one being extended.
