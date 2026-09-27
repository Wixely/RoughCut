# Bounded edit-and-export workflow

- Updated: 2026-09-24
- Owner: Implementation agent
- Review: Before expanding the format matrix, timing policy or export implementation
- Status: Executed on Windows managed and NativeAOT CLI; [strict evidence](evidence/2026-09-19-export.md), [delivery evidence](evidence/2026-09-22-delivery-encode.md)

There are two output paths, and they claim different things. **Strict export** proves that retained material reaches the output untouched, and refuses everything it cannot prove. **Delivery** always re-encodes, renders anything FFmpeg decodes, and claims a faithful edit rather than an untouched copy. Everything from here to "Delivery" describes the strict path; delivery has its own section and its own limits.

## Supported slice

This is a feasibility exporter with deliberately explicit limits, not general-purpose video export. It accepts a single local Matroska source containing exactly one PNG, FFV1 or H.264 video stream and one 48 kHz mono/stereo `pcm_s16le` audio stream. Both start at zero and must have identical durations. Video must be unrotated SDR with square pixels, constant frame duration exactly representable in milliseconds, and consistent dimensions. Cuts must land exactly on displayed-frame boundaries. No implicit snapping occurs.

Source and output are each limited to 60 seconds, video to 1920x1080 and 6,000 frames, and the timeline to 32 clips. Tool output is capped at 16 MiB, diagnostics at 64 KiB and each tool execution at 60 seconds. Packet inspection, including PNG payload inspection, must fit that limit. Intermediate media has a 256 MiB aggregate budget; final media has a separate 256 MiB limit. Validation decodes PCM into bounded temporary files and compares it in 64 KiB blocks, rather than holding a whole decoded soundtrack in memory.

| Stream/edit | Copy eligibility | Encoding path |
| --- | --- | --- |
| PNG video | Every packet contains a complete RGB8 non-interlaced PNG datastream; PTS/DTS/display intervals agree; both cuts are frame boundaries; no crop | FFV1 when crop or copy eligibility requires it |
| FFV1 or H.264 video | Unsupported for copy in this slice, regardless of keyframe flags | Decode retained frames and encode losslessly to FFV1 |
| PCM audio | Packet timestamps and lengths exactly match sample counts, with both cuts on packet boundaries | Sample-accurate trim, reblock and PCM encoding when packet boundaries/rounding prevent copy |
| Crop | Requires encoding | RGB conversion before exact pixel crop; all clips must produce the same dimensions |
| Timed PNG still | Never copied as a video packet | Scale with `contain` or `cover`, apply the optional crop, hold for an exact frame count and encode to FFV1; synthesize matching 48 kHz PCM silence |
| Applied voice replacement | Never copied as audio | Resample generated PCM WAVE, apply explicit exact or bounded `time-stretch` fitting, replace only the selected sample interval and encode the complete PCM timeline |

Per-stream decisions apply consistently across the output: if any video clip needs encoding, all video clips use FFV1, keeping concat codec parameters consistent. Audio can remain copied. Source PCM container timestamps may differ from exact sample positions by at most one millisecond for the encoding path; larger gaps/drift are rejected. Output audio samples must exactly match the selected source samples. Hard cuts may still have audible discontinuities; no crossfade is inserted.

MP4, compressed audio (including AAC/priming), video-only sources, VFR/gaps, nonzero origins, chapters, extra streams, multiple source videos, image-only timelines and general silence edits are rejected by strict export, because it cannot prove a copy for them; most of them deliver instead. A timed PNG can be inserted only into a timeline with one supported active video/audio source. Its duration must align exactly with the source frame cadence and 48 kHz sample grid, and it always selects complete FFV1/PCM rendering. PNG input is limited to 8 MiB, 8-bit RGB/RGBA, non-interlaced, at most 8K pixels. Chapter-bearing inputs are rejected instead of silently losing chapters; ordinary container metadata is intentionally omitted from output.

Applied voice replacement is bounded to corrected, non-overlapping speech under `require-isolated-dialogue`. `exact` fitting requires equal requested and generated durations. `time-stretch` permits a generated/requested tempo ratio from 0.8 through 1.25. Every interval must be fully retained exactly once inside one video clip. Partial cuts, duplicated source use, overlaps and changed generated assets are rejected. Rendering resamples the preview to the source layout, fits to the exact target sample count and replaces all source sound in the interval. There is no source separation, background preservation inside the interval, crossfade or automatic caption-text rewrite.

## Commands

With supported media and a UTF-8 SRT file inside the project directory:

```powershell
dotnet run --project src/RoughCut.Cli -- create artifacts/demo/media/source.mkv artifacts/demo/project.json
dotnet run --project src/RoughCut.Cli -- captions artifacts/demo/project.json source-1 artifacts/demo/source.srt 1
dotnet run --project src/RoughCut.Cli -- edit artifacts/demo/project.json examples/cut-and-reorder.json 2
dotnet run --project src/RoughCut.Cli -- preflight artifacts/demo/project.json
dotnet run --project src/RoughCut.Cli -- export artifacts/demo/project.json artifacts/demo/export
```

The example operations require a source of at least three seconds, a project time base of `1/1000`, and the initial `clip-1` ID emitted by `create`. They split at one and two seconds, remove the middle slice, trim the final slice to three seconds, and reorder the retained slices to `[2s,3s)` then `[0s,1s)`. They select strict copy-only mode. Supply your own operations for different timing or clip IDs. Times in edit files are **project ticks**, not seconds.

`captions` and `edit` each advance the revision once, compare against the supplied expected revision and save atomically. A failed/stale batch leaves the previous project intact. The supplied edit batch is applied in order, so later operations can reference IDs created by earlier splits. Supported action shapes:

```json
[
  { "action": "trim", "clipId": "clip-1", "in": 100, "out": 900 },
  { "action": "set-range", "clipId": "clip-1", "in": 0, "out": 1000 },
  { "action": "split", "clipId": "clip-1", "at": 500, "newClipId": "clip-2" },
  { "action": "reorder", "order": ["clip-2", "clip-1"] },
  { "action": "crop", "clipId": "clip-1", "crop": { "x": 0, "y": 0, "width": 80, "height": 48 } },
  { "action": "crop", "clipId": "clip-2", "crop": { "x": 8, "y": 4, "width": 80, "height": 48 } },
  { "action": "insert-image", "clipId": "title-card", "assetId": "image-a1", "duration": 1000, "beforeClipId": "clip-1", "fit": "contain" },
  { "action": "insert-clip", "clipId": "clip-1", "assetId": "source-1", "in": 0, "out": 1000, "beforeClipId": "clip-2", "audio": "source" },
  { "action": "export-mode", "mode": "prefer-stream-copy" }
]
```

Use `remove` with `clipId` to delete a clip, or `crop` with a null crop to clear it. `insert-clip` is its inverse: it puts a video clip back with an explicit `clipId`, `assetId`, `in`, `out` and optional `crop`, `fit`, `audio` and `beforeClipId`, so an interactive editor can offer removal and still undo it exactly. It restores material inside a video source only — image holds use `insert-image` — and never reaches past the asset duration. `insert-image` requires an imported image asset, a new clip ID and a positive hold duration in project ticks; omit `beforeClipId` to append it. Its audio policy is fixed to silence. Reorder must list every remaining clip exactly once. Trim only shrinks the current interval. `set-range` replaces a clip's retained interval anywhere inside its source, so it can also restore material an earlier trim dropped; it keeps image holds starting at zero and never reaches past the asset duration. Use it when an interactive editor must reverse a trim, and keep `trim` where a batch must be unable to extend a clip. Use a saved prior project as a candidate with the next revision to restore an earlier edit; the desktop review host keeps its own session undo history.

`preflight` prints a JSON plan and exits nonzero for unsupported requests. The plan includes the canonical project hash/revision, requested/resolved boundaries, frame/sample ranges, output mapping, dimensions and each stream's copy/encode reason. It verifies the used media and caption-source hashes. It does not trust a keyframe flag as proof of independent decoding.

If a supported plan requires encoding, choose `prefer-stream-copy` or `exact-edit`, review the plan, then explicitly authorize encoding:

```powershell
dotnet run --project src/RoughCut.Cli -- export artifacts/demo/project.json artifacts/demo/encoded-export --allow-encode
```

The flag cannot override `copy-only` mode or another unsupported result. Export recomputes preflight from the loaded project snapshot, rather than trusting an earlier plan. Media and caption-source hashes are checked again before publication. The report identifies the exact snapshot even if another edit is saved while export runs.

## Delivery

Delivery ([decision 0020](decisions/0020-delivery-encode-export.md)) renders the retained timeline into one H.264/AAC MP4 by decoding, cutting and re-encoding. It exists because the strict matrix above refuses ordinary acquired media — an AV1/Opus download or an H.264/AAC MP4 cannot be exported at all — and a person still needs a file they can send.

One FFmpeg filter graph does the whole job: each clip is trimmed out of its source with `trim`/`atrim`, cropped if the clip has a crop, fitted into one frame size with its `contain` or `cover` policy, and the results are concatenated. Cuts and ordering are therefore applied exactly once, by the encoder, from decoded frames. Audio is resampled to 48 kHz stereo. Video is `libx264` at CRF 20, audio is AAC at 192 kbit/s, with `+faststart` and no source metadata or chapters.

```powershell
dotnet run --project src/RoughCut.Cli -- preflight-delivery artifacts/demo/project.json
dotnet run --project src/RoughCut.Cli -- deliver artifacts/demo/project.json artifacts/demo/delivery
```

`preflight-delivery` starts no process at all: it maps the timeline, checks the bounds and reports the frame size, clips and duration a delivery would produce, so the cost of an encode is only paid deliberately. It exits nonzero when delivery would refuse.

Bounds are those of a delivery tool rather than a proof: 1 to 400 clips, at most four hours of output and 8 GiB. The first clip sets the delivered frame size, rounded down to even dimensions because H.264 requires them; other clips are fitted into it, so a timeline spanning differently sized sources still produces one file. A crop rounds down the same way and, where it then matches the delivered frame, the picture is cut straight out of the source rather than scaled into it: dropping one column keeps the remaining pixels, where fitting 135 into 134 would resample all of them.

The review window exports through this path — see the [desktop guide](desktop.md#exporting) — so a finished edit can leave the tool without dropping to the command line.

Delivery refuses, naming the reason, what it cannot render faithfully in this slice: timed image holds and applied voice replacements, both of which the strict path renders and validates. A missing source file, an empty or oversized timeline and an over-long output are refused before anything runs. A source that carries no audio is rendered with generated silence and named in `silencedAssets` rather than quietly losing its audio track.

What delivery checks before publishing: the delivered file really is H.264 and AAC, it is non-empty and inside the size budget, its duration matches the plan's within the frame each cut lands inside, and every source fingerprint matches the project both before the encode and before publication. What it does not check, and does not claim: frame hashes, sample equality or packet payloads. The bundle is `video.mp4`, `delivery.json` and, when cues survive, `captions.srt`, published by the same staging-and-rename that the strict path uses.

A delivered file is a re-encode. It is not evidence about its source, and where that evidence is the point, use the strict bundle.

## Mux

Mux ([decision 0022](decisions/0022-stream-copy-mux-export.md)) copies the retained material into a new container without decoding it. It is neither the strict export nor delivery: it makes no validated-matrix claim and touches no pixel, so it finishes in seconds and the packets it keeps are the source's own.

The cost is where the cuts land. A video copy can only begin at a keyframe, so each segment's **start** snaps to one; the **end** stays exactly where it was asked for, because a copy can stop anywhere. The plan names `requestedIn`/`requestedOut` beside the anchors used and the signed offset between them, so the error is stated rather than implied.

```powershell
dotnet run --project src/RoughCut.Cli -- cut-points artifacts/demo/project.json source-1 253
dotnet run --project src/RoughCut.Cli -- preflight-mux artifacts/demo/project.json mkv
dotnet run --project src/RoughCut.Cli -- mux artifacts/demo/project.json artifacts/demo/copied mkv
```

`cut-points` reports the keyframe anchors behind and ahead of a time, how far each sits from it, whether an audio packet starts within half a packet of it, and the source's own median keyframe interval — read from a bounded window of packets without decoding, in about a second on a file whose full index takes over two minutes. `audioAligned` is `null`, not `false`, where the audio was not sampled at that point. Use it to see the choice before making it: snap to an anchor and copy, or accept that this boundary needs delivery's re-encode.

`preflight-mux` resolves every boundary onto an anchor and prints the plan. Unlike the delivery preflight it does read the media, because anchors are a property of the file, but only a bounded window per boundary.

Bounds are 200 segments and 8 GiB. One FFmpeg run does the whole job from a single `ffconcat` list of `inpoint`/`outpoint` pairs: cutting each segment separately and concatenating them inflated the result, because each part was rounded outward and the errors accumulated.

Two properties are checked rather than assumed. Every published output must begin on a keyframe — a copy that starts elsewhere writes a file whose picture begins late, with no error from the muxer. And where two copied segments meet, audio packets are whole and do not align with the picture cut, so the muxer moves some of them forward; those adjustments are counted in `joinAdjustments`, while any other FFmpeg warning still fails the export. The packets themselves are untouched; only where they sit changes.

A mux output is fast and bit-exact for what it keeps. It is not evidence about frame-exact editorial intent: where the boundary matters more than the pixels, use delivery.

## Captions and output validation

Import accepts plain SRT cue numbers, `HH:MM:SS,mmm` timestamps and multiline nonempty text, up to 1 MiB and 10,000 cues. Original bytes stay untouched; project JSON records the relative path/hash and cues in their original millisecond time base. Imported cues must fall within their source duration. Malformed input is rejected. Caption text is data, never a command or inference instruction.

For each retained slice, intersect caption intervals with its source interval and map them to output time. Removed cues disappear; crossing cues split, and repeated/reordered slices repeat/reorder their cues. Full cue text is retained for partial cues because there is no word alignment yet. JSON retains exact rational timing; the SRT sidecar rounds starts down and ends up to milliseconds. Captions are not burned into video or added as a hidden encode operation in strict mode.

The new strict output directory is an atomic bundle:

- `video.mkv`: validated copied/encoded streams.
- `captions.srt`: retimed captions when the project has a caption track; empty if no cues remain.
- `export.json`: requested/resolved mapping, stream decisions, source/output hashes, tool versions, exact retimed caption intervals and validation evidence.

Before publication, the exporter checks every output presentation timestamp and frame hash against the selected source/crop or fitted image, exact PCM samples against selected source intervals, fitted voice audio or generated image-clip silence, all sample/frame counts and A/V duration, and packet payload hashes for streams declared copied. Voice and image fingerprints are checked both before rendering and before publication. This includes both sides of every join and replacement boundary. FFmpeg success alone does not publish an output. Files are staged next to the destination and the directory is renamed only after validation; collisions, tool failures and cancellation remove staged artifacts and preserve the source. Network filesystems, sudden power loss and disk-full recovery remain untested.

## Verification and next action

Run `.\scripts\verify.ps1 -PublishAot` for the synthetic regression suite. The export fixture has visible binary frame IDs, a changing-frequency audio signal and supplied captions. The independent frame-ID check verifies the reorder around the join; production validation additionally checks every pixel hash, selected PCM sample and copied packet payload. Delivery is checked against the same fixture re-encoded to H.264/AAC, reading the frame identifiers back out of the delivered MP4; the [delivery evidence](evidence/2026-09-22-delivery-encode.md) records what that does and does not prove.

Next owner/action: **Implementation agent: deliver a real acquired long-form project, offer delivery from the review window, and measure speaker/overlap error when representative fixtures are available; Wixely: provide representative labelled examples**. Extend codec/container, background-aware replacement or image-compositing coverage only with equivalent join, pixel and audio evidence.
