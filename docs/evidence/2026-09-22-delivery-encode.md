# Delivery encode evidence

- Date: 2026-09-22
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, FFmpeg 2026-09-17-git-7070fe638e (gyan full build)
- Scope: first delivery export path — H.264 video and AAC audio in MP4, beside the untouched strict copy export
- Decision: [0020](../decisions/0020-delivery-encode-export.md)

## Regression suite

`69 passed; 0 failed` with `--media --cli --desktop --mcp`, run out of a relocated output path because the review window from the previous session still held the desktop binaries. Three checks are new, and the existing CLI check grew a delivery section.

**Delivery renders a timeline the strict matrix refuses, in order and at the claimed length.** The fixture is the identified PNG/PCM source re-encoded to H.264 + AAC in MP4 — material the strict planner rejects, which the same check asserts before delivering. The timeline retains `[2s,3s)` then `[0s,1s)` of a four-second source. The delivered file holds exactly 20 frames, and reading the visible binary frame identifiers out of the decoded picture gives 20–29 then 0–9: the cut and the reorder survived the encode, in the right order, from the right intervals. The report's `expectedSeconds` is 2 and the measured duration is within 0.05 s of it. The published bundle is three files; `outputSha256` and `outputBytes` match the file on disk; the source fingerprint is unchanged. Delivering into an existing directory raises, a project whose asset hash no longer matches is refused, and neither leaves a published or staged directory behind.

**Delivery crops, silences and refuses what it cannot render faithfully.** A crop of 81×48 delivers an 80×48 file — odd widths cannot be encoded as H.264, and delivery rounds down rather than inventing a column — confirmed by probing the delivered stream, not only by reading the plan. A second clip asking for silence decodes to a peak below 256 of 32,768 while the first clip's audio peaks above 2,000, so the silence is real and the source audio was not lost. Timed image holds, missing media and applied voice replacements are each refused by code, and the refusal publishes nothing.

**MCP delivery and image export jobs complete, persist and cancel safely.** `roughcut_preflight_delivery` reports the timeline supported, and `roughcut_start_delivery` runs to `succeeded` with `mode: "delivery"` and publishes `video.mp4`. The tool-surface check now lists 28 tools.

**CLI.** `preflight-delivery` returns a supported two-clip plan and `deliver` publishes a bundle; delivering twice into the same directory fails.

## Independent check outside the harness

A separate run against a synthetic 320×240 25 fps H.264/AAC MP4 delivered a single cropped clip covering source seconds 2–4. Comparing the delivered picture against the source interval with the same crop gives PSNR 44.4 dB average; comparing it against seconds 0–2 gives 17.7 dB. The delivered frames come from the interval the timeline asked for, not from the start of the file. This was an ad-hoc run, not part of the suite.

The same run showed why the duration tolerance is not fixed: a cut that does not land on a frame boundary quantises to the frame containing it, so a 2.5 s timeline delivered 2.6 s at 10 fps. The check allows 0.25 s plus 0.05 s per clip and would still catch a lost or duplicated clip.

## Limitations

Delivery proves duration, codecs and provenance, not pixels or samples: there is no frame-hash or PCM comparison as there is in the strict path, and the evidence above for content fidelity is the frame-identifier oracle and the ad-hoc PSNR run, not a per-frame check in production. Nothing has been delivered from a real acquired source yet — no AV1/Opus download, no long file, nothing near the four-hour or 8 GiB bounds, and no multi-source timeline. Frame rate is passed through as variable; a timeline mixing sources of different rates has not been rendered. Rotation metadata is dropped. Linux is unverified.
