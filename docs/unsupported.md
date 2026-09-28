# What RoughCut does not do

- Added: 2026-09-24
- Owner: Implementation agent
- Review: Whenever a slice widens what is supported, or an acceptance run finds a new boundary
- Status: Collected on 2026-09-24 from the guides and evidence linked below

Every limit here is enforced or recorded somewhere else; this page exists so nobody has to read six documents to find out whether their material will work. Where a limit is checked in code, the tool says so and refuses rather than guessing. Where it is merely untested, that is stated as untested — which is not the same as broken.

## Getting material in

- One local file or one URL at a time. Playlists are refused; acquisition is bounded in size and time.
- Subtitles come from yt-dlp's own SRT output. Other caption formats must be converted before import, and imported SRT is capped at 1 MiB and 10,000 cues.
- Local transcription is the pinned Whisper `base.en` model. Other sizes, other languages and multi-hour resource profiles are unverified.
- Diarization needs sherpa-onnx models the user configures. With none configured it reports that safely; it never guesses a model path.
- Voice synthesis reaches a Qwen service on loopback only. There is no hosted provider.

## Editing

- One schema, version 1. A project that does not validate is refused with the offending fields named.
- Timelines hold video clips and timed PNG stills. Audio-only clips are not supported.
- Every video clip on a timeline must produce the same output canvas, so crops must agree in size.
- The review window will not remove the last clip on a timeline, because nothing in the window could put one back. CLI and MCP callers may empty a timeline.
- A boundary drag trims one clip. It does not move a cut between two adjacent clips, so closing a gap takes two drags.
- Removal, trim, split, reorder and crop are reversible in the window. Image insertion and voice replacement are CLI and MCP operations.

## Playback in the review window

- The player decodes VP9 video and Opus audio in WebM, and nothing else. Every source is copied once into that form for preview.
- Preview playback is an approximation, and says so: boundary jumps land on the decoder's nearest frame, audio is not gapless across a jump, and the copy is re-encoded. It is not evidence of what export produces.
- Replacing the player's own control bar with a timeline transport dropped its fullscreen control.
- The clipping of a cropped clip while it plays has not been observed — it needs decoders and a prepared copy, which no headless check has. See the [cropped playback evidence](evidence/2026-09-24-cropped-playback.md).
- Physical audio-device output and A/V sync are unverified; the only drift measurement is a headless dummy-device probe.

## Export

Two paths, claiming different things — the [export guide](export.md) has the full matrix.

**Strict copy** proves retained material reaches the output untouched, and refuses everything it cannot prove: one Matroska source with one PNG, FFV1 or H.264 video stream and one 48 kHz `pcm_s16le` audio stream, both starting at zero with equal durations, unrotated SDR with square pixels, constant frame duration exactly representable in milliseconds, cuts exactly on displayed-frame boundaries, at most 32 clips and 60 seconds. MP4, compressed audio, video-only sources, variable frame rates, gaps, nonzero origins, chapters, extra streams and multiple source videos are all refused. No cut is ever silently snapped.

**Delivery** re-encodes to H.264 and AAC in MP4, so it renders anything FFmpeg decodes, and claims a faithful edit rather than an untouched copy. It does not render timed image holds or applied voice replacements, both of which the strict path does. It checks codecs, size, duration and source fingerprints — not pixels or samples. It drops rotation metadata, passes frame rate through as variable, and has never been run on a timeline mixing sources of different rates or near its four-hour and 8 GiB bounds. It has now delivered a 13-minute 1440×1080 source cut to 40 clips.

**Reading a source** happens two ways, and they answer different questions. `ProbeAsync` asks the container for codec, dimensions, time base and length, and is what creating a project, preparing playback and listing export formats use: seconds on a long file. `InspectAsync` walks every frame, which a frame-exact render and the strict export path need, and which costs minutes on a fourteen-minute 1080p60 source and refuses one with more than 100,000 frames outright. Nothing that only needs a source's shape should call the second.

**Delivery formats** are four and closed: MP4, MKV and MOV with H.264/AAC, and WebM with VP9/Opus. Each output is probed for the codecs its target claims before publication, and a format outside that list is refused by name rather than attempted. WebM encoding is slow and nothing warns about how slow.

**Mux** copies packets and decodes nothing, so it is fast and bit-exact for what it keeps — and it moves every segment's start onto a source keyframe, up to one keyframe interval from where it was asked for. The plan states each offset; nothing is snapped silently. Segment ends stay exact. It checks that the output begins on a keyframe and counts the timestamp adjustments the muxer makes at a join, where whole audio packets meet a picture cut, and it checks nothing else: no frame hash, no sample comparison, no packet payload. It is not evidence about frame-exact editorial intent, and where the boundary matters more than the pixels, delivery is the path. Bounds are 200 segments and 8 GiB.

**Speech timings are estimates, at both ends of the error.** Local transcription overlaps its windows so a chunk boundary stops inventing speech, and reports how many fragments it discarded — but `base.en` still misses sung words it does not hear as speech. Cutting every transcribed segment out of a music video therefore leaves vocal material behind. A transcript is a starting point for an edit, not a ground truth about a source.

Neither path burns captions into the picture. Both write an SRT sidecar instead.

## Platforms and packaging

- Windows is the only platform anything has been run on. Linux is unverified and macOS has never been attempted.
- The CLI has been published NativeAOT before, but cannot be rebuilt that way on the current machine: the MSVC platform linker is absent.
- The MCP host is a normal managed deployment. Its reflection-based schema generation and the native speech runtimes have not been qualified for NativeAOT.
- FFmpeg, FFprobe, yt-dlp and Deno are external tools that must be installed; RoughCut ships none of them. See [dependencies](dependencies.md).
- Network filesystems, sudden power loss and disk-full recovery are untested for the atomic export bundle.

## Things that are deliberately not automatic

- Analysis proposals retain uncertain material by default. Removal happens only under an explicit policy, bound to a revision.
- Provider-supplied analysis, captions and transcripts are treated as data, never as instructions, and no MCP tool executes what a provider sends.
- Nothing renders video while editing except an explicit action: preview is approximated over one cached copy, and only **Render exact preview** and export produce media.
