# 0020: Delivery encode export beside the strict copy path

- Date: 2026-09-22
- Status: Accepted for the first delivery slice
- Supersedes: nothing. It extends export policy; 0003 and 0011 still govern the strict path.

## Context

The validated exporter in 0003 proves that retained material reaches the output untouched: it compares packet payloads for copied streams, every presentation timestamp, every decoded frame hash and every PCM sample. That proof is only possible inside a narrow matrix — one Matroska source, PNG/FFV1/H.264 video, `pcm_s16le` audio, at most 32 clips, at most 60 seconds — so it refuses almost everything a person actually acquires. The project the review window opened on 2026-09-21 (AV1 video, Opus audio, Matroska, from a URL) cannot be exported at all, and neither can an ordinary H.264/AAC MP4.

The desktop review slice (0019) already separates approximation from truth for playback. Delivery is the same separation for output: people need a file they can send, and the tool should not have to claim that file is a byte-faithful copy in order to produce it.

## Decision

Add a second export path, `delivery`, that always re-encodes to H.264 video and AAC audio in MP4. It never copies source packets and never claims to. It renders one filter graph — per-clip `trim`/`atrim`, the clip's crop, its `contain`/`cover` fit into one frame size, then `concat` — so cuts and ordering are applied exactly once, by the encoder, from decoded frames.

The strict path is untouched. Its matrix, its gates, its report and its refusals are unchanged, and `copy-only` still means copy-only.

What delivery claims is narrower than what the strict path claims, and is stated in the report: the cuts, ordering, crops and captions follow the timeline, and the delivered duration matches the timeline's, within the frame each cut lands inside. Before publishing it checks the delivered codecs, the file size, the duration against the plan, and every source fingerprint both before the encode and before publication. Nothing else about the pixels is proven — no frame hashes, no sample comparison. The report says so rather than implying more.

Its bounds are those of a delivery tool rather than a proof: 1 to 400 clips, up to four hours of output, up to 8 GiB. Preflight is pure — it never starts FFmpeg — so an agent or the window can show what delivery would do before paying for an encode.

Delivery refuses, with a message naming the reason, what it cannot render faithfully in this slice: timed image holds and applied voice replacements, both of which the strict path already renders and validates. A source that carries no audio at all is rendered with generated silence and named in `silencedAssets`, because dropping the audio stream silently would change what the file is without saying so.

The surface is the same everywhere: `preflight-delivery` and `deliver` on the CLI, `roughcut_preflight_delivery` and `roughcut_start_delivery` over MCP, and `mode: "delivery"` on the durable job record, which reads back as `strict` for every checkpoint written before today.

## Consequences

- Real acquired media can now leave the tool as a file, which the strict path could not do.
- A delivered file is a re-encode. It is not evidence about the source, and it should not be used where the strict bundle's proof is the point.
- Two export paths must be kept honest about which one made a given output. The bundle names are different (`video.mp4` with `delivery.json`, against `video.mkv` with `export.json`), and each report carries its own policy string.
- Crop on a delivered clip is applied to the picture; the review window still cannot show a crop on the moving picture, so a person may see an uncropped preview and a cropped delivery.
- Rotation metadata is dropped, as in the strict path, so a source with a rotation side-data tag would deliver unrotated. No such source has been rendered yet; the frame reader refuses them earlier.

## Evidence

`docs/evidence/2026-09-22-delivery-encode.md`. The regression suite delivers an H.264/AAC MP4 the strict path refuses, reads the visible binary frame identifiers out of the delivered file to prove the cut and the order survived the encode, checks the odd-width crop rounds down to an encodable frame, checks a clip asking for silence delivers silence, and checks that refusals and failures publish nothing.

## Review trigger

Before adding a codec, a container, image holds or voice replacement to this path, or before any caller treats a delivered file as evidence about its source.
