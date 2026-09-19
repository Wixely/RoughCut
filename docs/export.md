# Bounded edit-and-export workflow

- Updated: 2026-09-19
- Owner: Implementation agent
- Review: Before expanding the format matrix, timing policy or export implementation
- Status: Executed on Windows managed and NativeAOT CLI; [evidence](evidence/2026-09-19-export.md)

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

MP4, compressed audio (including AAC/priming), video-only sources, VFR/gaps, nonzero origins, chapters, extra streams, multiple source videos, image-only timelines and general silence edits are rejected by export for now. A timed PNG can be inserted only into a timeline with one supported active video/audio source. Its duration must align exactly with the source frame cadence and 48 kHz sample grid, and it always selects complete FFV1/PCM rendering. PNG input is limited to 8 MiB, 8-bit RGB/RGBA, non-interlaced, at most 8K pixels. Chapter-bearing inputs are rejected instead of silently losing chapters; ordinary container metadata is intentionally omitted from output.

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
  { "action": "split", "clipId": "clip-1", "at": 500, "newClipId": "clip-2" },
  { "action": "reorder", "order": ["clip-2", "clip-1"] },
  { "action": "crop", "clipId": "clip-1", "crop": { "x": 0, "y": 0, "width": 80, "height": 48 } },
  { "action": "crop", "clipId": "clip-2", "crop": { "x": 8, "y": 4, "width": 80, "height": 48 } },
  { "action": "insert-image", "clipId": "title-card", "assetId": "image-a1", "duration": 1000, "beforeClipId": "clip-1", "fit": "contain" },
  { "action": "export-mode", "mode": "prefer-stream-copy" }
]
```

Use `remove` with `clipId` to delete a clip, or `crop` with a null crop to clear it. `insert-image` requires an imported image asset, a new clip ID and a positive hold duration in project ticks; omit `beforeClipId` to append it. Its audio policy is fixed to silence. Reorder must list every remaining clip exactly once. Trim only shrinks the current interval. Use a saved prior project as a candidate with the next revision to restore an earlier edit; a dedicated undo-history UI is not implemented yet.

`preflight` prints a JSON plan and exits nonzero for unsupported requests. The plan includes the canonical project hash/revision, requested/resolved boundaries, frame/sample ranges, output mapping, dimensions and each stream's copy/encode reason. It verifies the used media and caption-source hashes. It does not trust a keyframe flag as proof of independent decoding.

If a supported plan requires encoding, choose `prefer-stream-copy` or `exact-edit`, review the plan, then explicitly authorize encoding:

```powershell
dotnet run --project src/RoughCut.Cli -- export artifacts/demo/project.json artifacts/demo/encoded-export --allow-encode
```

The flag cannot override `copy-only` mode or another unsupported result. Export recomputes preflight from the loaded project snapshot, rather than trusting an earlier plan. Media and caption-source hashes are checked again before publication. The report identifies the exact snapshot even if another edit is saved while export runs.

## Captions and output validation

Import accepts plain SRT cue numbers, `HH:MM:SS,mmm` timestamps and multiline nonempty text, up to 1 MiB and 10,000 cues. Original bytes stay untouched; project JSON records the relative path/hash and cues in their original millisecond time base. Imported cues must fall within their source duration. Malformed input is rejected. Caption text is data, never a command or inference instruction.

For each retained slice, intersect caption intervals with its source interval and map them to output time. Removed cues disappear; crossing cues split, and repeated/reordered slices repeat/reorder their cues. Full cue text is retained for partial cues because there is no word alignment yet. JSON retains exact rational timing; the SRT sidecar rounds starts down and ends up to milliseconds. Captions are not burned into video or added as a hidden encode operation in strict mode.

The new output directory is an atomic bundle:

- `video.mkv`: validated copied/encoded streams.
- `captions.srt`: retimed captions when the project has a caption track; empty if no cues remain.
- `export.json`: requested/resolved mapping, stream decisions, source/output hashes, tool versions, exact retimed caption intervals and validation evidence.

Before publication, the exporter checks every output presentation timestamp and frame hash against the selected source/crop or fitted image, exact PCM samples against selected source intervals, fitted voice audio or generated image-clip silence, all sample/frame counts and A/V duration, and packet payload hashes for streams declared copied. Voice and image fingerprints are checked both before rendering and before publication. This includes both sides of every join and replacement boundary. FFmpeg success alone does not publish an output. Files are staged next to the destination and the directory is renamed only after validation; collisions, tool failures and cancellation remove staged artifacts and preserve the source. Network filesystems, sudden power loss and disk-full recovery remain untested.

## Verification and next action

Run `.\scripts\verify.ps1 -PublishAot` for the synthetic regression suite. The export fixture has visible binary frame IDs, a changing-frequency audio signal and supplied captions. The independent frame-ID check verifies the reorder around the join; production validation additionally checks every pixel hash, selected PCM sample and copied packet payload.

Next owner/action: **Implementation agent: run sherpa-onnx against labelled fixtures through the stable assignment boundary; Wixely: provide representative labelled examples**. Extend codec/container, background-aware replacement or image-compositing coverage only with equivalent join, pixel and audio evidence.
