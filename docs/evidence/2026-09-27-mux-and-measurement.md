# Copying, measuring and transcribing a real source

- Date: 2026-09-27
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, FFmpeg/FFprobe 8.0, yt-dlp 2026.09.08 with Deno 2.9.7, Whisper.net 1.9.1 `base.en`
- Scope: one 13-minute 1440×1080 music video, acquired from a URL and cut down twice, entirely through the MCP host
- Records: [decision 0022](../decisions/0022-stream-copy-mux-export.md), [0023](../decisions/0023-measurement-inside-roughcut.md), [0024](../decisions/0024-overlapping-transcription-windows.md)

`81 passed; 0 failed` with `--media --cli --desktop --mcp`.

The source is a real commercial music video, so it is not in the repository and no lyric or dialogue text appears here or in any committed file. Everything below is timings, counts and measurements.

## What was asked for, and what RoughCut could not do

Two edits of the same source, driven by an MCP client through a JSON-RPC stdio driver: cut out the non-music sections, and then cut out everything where anyone speaks or sings. The output had to be muxed rather than re-encoded, with a project file to open and check afterwards.

Both edits were produced. Five things had to be built first, and each of them existed because the run failed without it:

1. **The download picked a rendition nobody could afford.** The default selection took a 604 MiB 4K AV1 stream, hit the 512 MiB media bound part-way through, and left a `.part` file beside the audio. Acquisition then reported "Sequence contains more than one matching element", because `SingleOrDefault` throws on two matches and the `?? throw` beside it only covered zero. The caller now lists renditions and names one, or a bitrate policy; a rendition that cannot fit is refused before the download starts; a partial file is named as one.
2. **Nothing could find the sections.** The first attempts measured loudness in a script beside the project and put the cut in the wrong place every time, because the source opens with a loud orchestral score and level alone cannot distinguish that from a loud pop record. `audio-profile` measures the share of energy below a band split instead: drums and bass enter at 253 s, where the share moves from 0.008 to 0.435 while the level barely changes.
3. **A copy could not be aimed.** Input-side FFmpeg seeking overshot by exactly one keyframe interval — asked for 5 s, produced 9.207 s; asked for 3 s, produced 7.207 s, with a measured 4.207 s interval. Output-side seeking from a non-anchor start wrote a file whose first picture was at 2.839 s with nothing before it and no error. `cut-points` now reports the anchors around a time in about 1.2 s, against over 120 s for a full index of the same file, and every published mux output is checked to begin on a keyframe.
4. **Cutting each segment separately inflated the result.** A 5.0 s cut produced 5.406 s and a 7.0 s pair produced 7.406 s, because each part was rounded outward. One `ffconcat` list of `inpoint`/`outpoint` pairs and one FFmpeg run resolves all the boundaries in a single timeline.
5. **Transcription refused the source outright.** One chunk arrived 7.5 ms short of its expected byte count against a 1 ms tolerance and aborted everything; and two separate files converted milliseconds to ticks by demanding an exact representation, which no source with a 1/12288 time base can satisfy. The tolerance is now 250 ms with the padding reported, and both conversions are the shared, named `TimeMath.NearestTicks` and `FloorTicks`. The duplicate is why two announced fixes changed nothing.

## The mux path on the real file

The music-only edit was copied, not encoded. The plan states each segment's requested boundary, the anchor its start moved to and the signed offset, and the report counts the join adjustments the muxer made where whole audio packets meet a picture cut. The output plays, carries the source's own packets, and was produced in seconds rather than the minutes delivery takes on the same timeline.

What this does not show: no frame hash, sample comparison or packet payload check is made by the mux path, and none is claimed. The starts sit on the source's keyframes, up to one keyframe interval from where they were asked for, and the plan is where that error is stated.

## Finding words, and finding a band

Cutting "everywhere anyone speaks or sings" needs both questions answered separately, and RoughCut answers each with its own tool. Local transcription says where words are; `audio-profile` says whether a band is playing under them. Joining the two classified all 152 segments of the first transcript into 51 over a quiet score and 101 over drums and bass, at a low-band share threshold of 0.12 — which is how the spoken-word sections of this particular source were separated from its sung ones without a second model.

## The chunk boundary was inventing speech

The first transcript's timings gave the artefact away: 27 of its 152 segments began within a quarter-second of a 30-second chunk boundary, at 27 of the 28 boundaries in the file, totalling 90.6 s — 22% of all the speech claimed. A model handed a cold window reports something at the start of it.

Cuts were at segment granularity, not chunk granularity — the median segment is 2.3 s — so this removed music in 27 places rather than removing 27 whole chunks. Had it been chunk-granular it would have taken 810 of the file's 822 seconds.

Adding a three-second prefix to each window cut that hard — 27 boundary-aligned segments became 5, one of which is the genuine opening line — but the total claimed speech barely moved, and a new spike appeared. It took two more runs to find why, and the answer is that the first fix was the wrong shape.

**The window must not exceed the engine's own frame.** Whisper encodes exactly 30 seconds at a time. A 33-second window is split internally, and the model starts cold again in the middle of it: 15 of the 28 windows produced a fragment at *exactly* 30.000 s into the window, which is 27 s into the chunk. Adding a suffix as well made it a 36-second window and 25 such fragments. Widening the window to make room for context creates the artefact it was meant to remove.

The context therefore comes **out** of the window. It stays at one 30-second frame, the span it owns shrinks to 24 s, and it advances by that span — so every millisecond is owned by exactly one window and heard by two.

| | windows | segments | claimed as speech | discarded | at exactly one frame into the window |
| --- | --- | --- | --- | --- | --- |
| isolated, 30 s window | 28 | 152 | 414.0 s | 0 | 26 at the window start |
| 3 s prefix, 33 s window | 28 | 140 | 383.9 s | 33 | 14 |
| 3 s both edges, 36 s window | 28 | 144 | 418.0 s | 40 | 15 |
| 3 s context, 30 s window | 35 | 150 | 429.2 s | 52 | 4 |

In the final run the kept segments are spread evenly across the window — 21, 22, 20, 14, 17, 16, 17, 22 starts per three-second band of the owned span — with no position favoured. That flatness is the result; the earlier runs all had a spike.

The transcript also moved in both directions, which is the point of measuring it rather than counting boundary hits. Against the isolated transcript, 23 segments disappeared and 23 appeared:

| | segments | total | median length | median words | median level | median low-band share |
| --- | --- | --- | --- | --- | --- | --- |
| shared | 127 | 373.3 s | 2.5 s | 4 | −16.9 dB | 0.203 |
| gone from the isolated run | 23 | 76.4 s | 2.6 s | **1** | −18.8 dB | 0.176 |
| new in the final run | 23 | 60.8 s | 2.6 s | 3 | −15.6 dB | **0.310** |

A 2.6-second segment containing one word, over quieter material, is what a fabricated fragment looks like; a three-word segment over a 0.310 low-band share is words with drums and bass under them. So the run with context both loses fabrications and finds sung words the cold-start transcript missed — measured with RoughCut's own band profile, not asserted.

## Checking the edit with the tool that made it

The no-vocals edit was verified the way the product should be: the delivered file was opened as a new project and transcribed, so RoughCut reports what survived its own cut. Both edits removed every transcribed segment padded by 0.25 s, merged, keeping the gaps.

| built from | output | clips | material still heard as words |
| --- | --- | --- | --- |
| prefix-only transcript | 408.3 s | 40 | 21 segments, 71.2 s (17%) |
| frame-bounded transcript | 370.4 s | 28 | 6 segments, 20.7 s (6%) |

The remaining 20.7 s is concentrated in five places, the longest a nine-word run at 163 s of the output. Delivery reported 370.463 s against a planned 370.365 s — inside the frame each cut lands in — and re-checked every source fingerprint before publishing.

This is a measurement of the cut, not a proof of it: the verification uses the same model that produced the edit, so a word it cannot hear at all is invisible to both. It bounds the *inconsistency* of the edit, and shows the work is reproducible from the project file alone.

## Limitations

The remaining error is the opposite one, and it is reduced here rather than fixed: `base.en` misses sung words it does not hear as speech. The first no-vocals edit left 71.2 s of material in the output that RoughCut itself hears as words; the edit built from the frame-bounded transcript leaves 20.7 s. Context recovers words the model nearly heard, but nothing recovers a word it cannot hear at all — that needs a larger model or a second signal.

The spoken/sung split rests on one threshold measured against one source. It is the caller's judgement, exercised here to show the measurements support it, not a policy RoughCut applies.

One source, one machine, one model. Nothing here measures a second codec, a longer file, or a source whose keyframe spacing is irregular.
