# 0024: Overlapping transcription windows

- Date: 2026-09-27
- Status: Accepted for local transcription
- Amends: [0006](0006-acquisition-caption-speech-boundary.md) and [0007](0007-live-acquisition-and-whisper.md), which chunked a source into isolated windows

## Context

Local transcription cuts a source into 30-second chunks and transcribes each one on its own. On a real 13-minute source that produced a transcript with an artefact visible from the timings alone: 27 of its 152 segments began within a quarter-second of a 30-second boundary, one at 27 of the 28 boundaries in the file. Those 27 segments accounted for 90.6 s — 22% of all the speech the transcript claimed.

The cause is not a bug in the chunking. A speech model handed a window with no history reports something at the start of it, because that is where its context begins; a musical entry or a held note at the top of a chunk becomes words. Isolated chunks give the model 28 cold starts and take each one at face value.

This mattered because those timings were driving cuts. Every fabricated segment removed real music from the edit.

## Decision

Each window carries `overlapSeconds` (3 by default) of context at each end, and owns only the span between those margins. Everything the model reports whose start falls outside the owned span is discarded, and the count is published as `DiscardedOverlapSegments`. Windows advance by the owned span, so every millisecond of the source is owned by exactly one window and heard by two.

The boundary between owned spans therefore sits in the middle of material the model has already heard, and the cold-start fragment lands in a margin — which belongs to a neighbour that saw that audio away from its own edge.

**The context is taken out of the window, never added to it.** This is the part that took two attempts to get right. Whisper encodes exactly 30 seconds at a time; a longer input is split internally and the model starts cold again in the middle of it. Widening a 30-second window to 33 seconds to make room for a prefix therefore created a *second* cold start at exactly 30 seconds into the window — fifteen of twenty-eight windows produced a fragment there, so the total fabrication barely moved. The window is capped at the engine's frame and the owned span shrinks to 24 seconds instead.

Details that are decisions rather than implementation:

- **The context is transcribed and then not believed.** Reusing a neighbour's result for the overlap would inherit its truncation at the far edge; transcribing and discarding costs a little audio and keeps each owned span's interior clean.
- **The default yields to a narrow window.** Twice the overlap must be less than the window, and the default is `min(3, chunkSeconds / 5)` rather than a fixed 3 that a five-second window cannot accommodate. Explicit values are validated; the default adapts.
- **The project records it.** `TranscriptionProvenance` gains `OverlapSeconds` and `DiscardedOverlapSegments`, both defaulting to zero so projects written before this still load. An opened project explains why its transcript is shorter than the raw output of the same model.
- **The caller can set it.** `roughcut_transcribe_local` takes `overlapSeconds`, because the tradeoff — 25% more windows against boundary fabrication — is the caller's. `chunkSeconds` keeps its meaning: the window handed to the engine, 5 to 30 seconds.

Two related failures on the same real source are fixed here because they blocked transcription entirely rather than degrading it:

- The chunk decoder demanded its expected byte count within one millisecond. The real source was 7.5 ms short on a chunk and the whole transcription aborted. The tolerance is now 250 ms, and what was padded is reported as `PaddedMilliseconds` rather than absorbed.
- Two files each converted milliseconds to project ticks by demanding an exact representation, so any source whose time base could not express its own length in whole milliseconds was refused. Both now use shared `TimeMath.NearestTicks` (for a model's estimate) and `TimeMath.FloorTicks` (for a measured length), named so the choice at each site is visible. The duplicate conversion is why the first two attempts at this fix changed nothing.

## Consequences

- On the real source the boundary artefact is gone rather than reduced: kept segments are spread evenly across their window (21, 22, 20, 14, 17, 16, 17, 22 per three-second band) where isolated windows put 27 of 152 in the first quarter-second.
- Both errors moved the right way, which the [evidence](../evidence/2026-09-27-mux-and-measurement.md) measures rather than assumes. Against the isolated transcript, 23 segments disappeared whose median length was 2.6 s and median content **one word** — the signature of a fabricated fragment — and 23 appeared with a median of three words over a low-band share of 0.310, which is material with drums and bass under it: sung words the cold-start transcript had missed.
- Transcription runs 35 windows on that source instead of 28, so it costs about 25% more. The owned span is 24 seconds where it was 30.
- What this does **not** fix is the opposite error. The model still misses sung words it does not recognise as speech — reported on the same source as "a few parts of the song with vocals snuck in at the end" — and no amount of context repairs a false negative. That needs a better model or a second signal, not a wider window.
- The no-vocals edit of the real source went from leaving 71.2 s of material RoughCut still hears as words to leaving 20.7 s, verified by transcribing the delivered file.
- `base.en` remains the pinned model, and transcription now refuses when its file is absent rather than starting a 147 MB download inside a transcribe: `roughcut_fetch_speech_model` exists to pay that cost deliberately.

## Evidence

`docs/evidence/2026-09-27-mux-and-measurement.md` records all four transcripts of the same source — isolated, prefix-only, both-edges and frame-bounded — with their window histograms, and what changed between them. The suite proves the mechanism directly: a fixture engine that emits a fragment at each edge of every window it is handed, asserted to survive only at the very start and end of the source, with no duplicated or out-of-order segment across the joins, the window never exceeding what was asked for, and the overlap recorded in the saved project.

## Review trigger

Before adopting any engine whose frame is not 30 seconds — `DefaultChunkSeconds` is that frame, not a preference — and before making the owned span adaptive. If a model is adopted whose cold-start behaviour makes the context unnecessary, measure the window histogram before removing it.
