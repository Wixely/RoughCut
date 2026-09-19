# 0003: Bounded export with complete content validation

- Date: 2026-09-19
- Status: Implemented within the user-authorized export feasibility slice
- Source: User instruction to perform the recommended edit/export work
- Owner: Implementation agent
- Review: Before expanding formats, snapping policy, audio processing or output layout

## Decision

Implement transactional JSON edit batches, portable supplied-SRT captions, preflight and export on the shared core/media APIs. Keep exact rational timing and retain requested/resolved boundaries separately. The first slice rejects non-frame-aligned boundaries rather than inventing a snapping tolerance.

Use a bounded Matroska PNG/PCM matrix to prove strict-copy cuts. Verify complete independently decodable RGB8 PNG datastreams per packet, exact PCM timing and packet boundaries. Do not adopt a general keyframe-based copy heuristic. FFV1/H.264 input video can use a validated decode/FFV1 encode path, while independently copyable audio stays copied. PCM trimming uses sample indices when copy boundaries do not suffice. Encoding needs an explicit headless policy (`--allow-encode`); this cannot override strict copy-only mode.

Publish a new directory containing media, optional retimed SRT and an export report only after all decoded frames, all PCM samples, presentation timing and copied packet payloads match the plan. Use generated concat filenames, argument arrays, bounded execution and same-parent staging; preserve source media and existing output destinations.

## Consequences

The [matrix and limits](../export.md) are intentionally narrow and do not establish general codec or platform safety. Lossless FFV1/PCM output is a feasibility artifact, not a compact consumer delivery preset. Future presets must be explicit decisions with new validation. Source metadata is omitted; embedded extra streams and chapters are rejected until they can be preserved/retimed deliberately. SRT output rounds only at serialization and keeps full text on partially retained cues until word alignment is available.

No new packages, integrations, runtimes or sibling-library changes were needed. [Windows managed and NativeAOT evidence](../evidence/2026-09-19-export.md) supports this implementation. The next work is shared jobs/MCP; full speech/evidence contracts and broader formats continue separately. Earlier sequencing records remain historical.
