# Delivery encode evidence

- Date: 2026-09-22, extended 2026-09-23 with the window export and the first real delivery
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, FFmpeg 2026-09-17-git-7070fe638e (gyan full build)
- Scope: delivery export path — H.264 video and AAC audio in MP4, beside the untouched strict copy export
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

## Exporting from the review window (2026-09-23)

**Review window exports the timeline to a deliverable MP4 beside the project** is a new harness check. The session publishes `desktop-timeline-project-export/video.mp4` for the revision it started from, reports the folder, size, length and revision, and leaves the project at the same revision it loaded — exporting never edits. The window offers the action and shows that outcome: the rendered document contains the **Export MP4** button and the model carries the session's status text. A second export publishes `-export-2` rather than overwriting the first. A cancelled export throws, publishes nothing, leaves no staging directory and says so. A timeline delivery cannot render — a timed image hold — is refused before any encode with "video clips only" in the message.

## First delivery of a real acquired project (2026-09-23)

The project acquired from a URL on 2026-09-21 — AV1 video, Opus audio, Matroska, 320×240 at 15 fps, retaining `[0, 1.893s)` of an 18.934 s source with a 135×140 crop — delivered in 0.75 s to a 134×140 H.264/AAC MP4 of 77,546 bytes, alongside its one surviving retimed caption. Strict preflight on the same project refuses it: "This export slice supports PCM signed 16-bit little-endian audio only". This is the first time a project acquired by RoughCut has produced a finished file.

Delivered length was 1.933 s against the timeline's 1.893 s: at 15 fps the cut falls inside a frame, and the frame containing it is kept whole. That is the quantisation the duration tolerance exists for.

Measuring picture fidelity against the source needs a control, and the first attempt did not have one. Comparing the delivered file against the cropped source with PSNR gave 21.4 dB luma, which looks alarming until the same comparison is run against a plain FFmpeg crop-and-encode of the same rectangle with the same settings and no RoughCut involvement: that control scores 22.0 dB. The delivered file matches the control at 25.9 dB. The low absolute number is the measurement — comparing a 15 fps source against a re-encode drifts frame to frame — not the pipeline. PSNR against a differently timed reference is not evidence of anything here; the frame-identifier oracle in the suite is.

That first measurement did find a real defect on the way. An odd crop width was being scaled into the even delivered frame and padded, resampling every pixel to lose one column. A crop now rounds down to even dimensions itself and, where it then matches the delivered frame, the picture is cut straight out of the source with no scaling at all.

## Limitations

Delivery proves duration, codecs and provenance, not pixels or samples: there is no frame-hash or PCM comparison as there is in the strict path, and the evidence above for content fidelity is the frame-identifier oracle and the ad-hoc PSNR run, not a per-frame check in production. One real acquired source has now been delivered, but it was two seconds of a nineteen-second download: nothing long, nothing near the four-hour or 8 GiB bounds, and no multi-source timeline. The window's export has not yet been driven by a person on a physical device — the harness drives the session, and the button's presence is checked in a rendered document rather than clicked. Frame rate is passed through as variable; a timeline mixing sources of different rates has not been rendered. Rotation metadata is dropped. Linux is unverified.
