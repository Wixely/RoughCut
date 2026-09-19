# 0014: Use CupriFace for the initial desktop review surface

- Date: 2026-09-19
- Status: Accepted for initial Windows review slice
- Review: CupriFace upgrade, live playback adoption, or Linux acceptance

## Context

RC-06 needs a small desktop surface over the same project, timing and job boundaries used by CLI and MCP. The sibling CupriFace source was inspected without modification. Its shell and binding model can render the required review controls, but the sibling checkout did not contain loadable `cupricodecs` native media assets when its MediaProbe sample was executed. That result does not establish live playback or audio synchronization.

CupriFace 0.26.1 and CupriFace.Shell 0.26.1 are public MIT-licensed release packages. Their package metadata identifies source commit `a91ab263697bc32eab4aece52e3d71b6bd692017`. Exact release archives and hashes are recorded under `vendor/nuget`.

## Decision

Use CupriFace 0.26.1 for the initial Windows desktop shell, binding and rendering layer. Keep media resolution in `RoughCut.Application`: transcript and evidence selection maps source timing through the exact saved revision and renders a bounded PNG through the existing FFmpeg adapter. Use the existing revisioned speaker edit API for label changes, including persisted inverse edits for undo and forward edits for redo.

Do not claim live playback from this slice. Evaluate synchronized video/audio playback separately with its native assets and runtime evidence before adopting it. Keep the main CLI NativeAOT gate independent of the desktop package; publish the desktop framework-dependent until a separate AOT experiment succeeds.

## Consequences

The product now has a reviewable desktop surface without duplicating project persistence or relaxing revision checks. Headless snapshots provide deterministic layout and real-frame regression coverage. Selecting a row currently blocks its UI handler while FFmpeg decodes, and crop information is descriptive rather than interactive. Continuous playback, audio sync, scrubbing, crop manipulation, interactive Windows execution and Linux remain RC-06 work.

## Evidence

See [2026-09-19 desktop review evidence](../evidence/2026-09-19-desktop-review.md).
