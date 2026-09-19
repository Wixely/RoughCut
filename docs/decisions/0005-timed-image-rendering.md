# 0005: Render timed PNG clips through the source canvas

- Date: 2026-09-19
- Status: Accepted for the bounded local MVP
- Review trigger: image-only timelines, overlays, transitions, new image formats, variable cadence, compressed audio or broader compositing

## Decision

Represent a generated/imported image as a normal ordered timeline clip with a positive project-time duration, `contain` or `cover` fit, an optional crop and explicit silence. Require one supported active video/audio source to define the output canvas, cadence and audio format. Import remains separate from timeline mutation; `insert-image` is revision checked and atomic.

Resolve timeline previews against an exact saved revision and use the same scale/crop filter construction as export. Active image clips always require the complete video stream to be encoded losslessly to FFV1 and the complete audio stream to be encoded as PCM, with 48 kHz silence for image intervals. Strict copy-only mode returns unsupported. Image holds must align exactly to both the source frame cadence and audio sample grid.

Validate PNG bounds, structure, dimensions and fingerprint before rendering and recheck the fingerprint before publication. Validate every rendered frame timestamp and pixel hash, every PCM sample including generated silence, and the complete A/V duration before atomically publishing an export bundle.

## Evidence and limits

The Windows synthetic regression proves transactional insertion, `contain` and `cover`, revision conflicts, CLI and MCP timeline preview, strict-copy rejection, explicit encoded export, caption shifting, fitted preview/export pixel equality, generated silence and changed-image rejection. The official MCP client receives the timeline preview as an image content block and completes a durable image export job. See [timed-image evidence](../evidence/2026-09-19-timed-images.md).

This decision does not establish image-only output, overlays on moving video, transitions, JPEG/WebP, alpha compositing, arbitrary duration rounding, multiple active video sources, Linux behavior or interactive AI-agent visual interpretation.
