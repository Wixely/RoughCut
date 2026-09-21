# 0018: Let yt-dlp use its default Deno runtime instead of clearing every runtime

- Date: 2026-09-21
- Status: Accepted for URL acquisition
- Supersedes: The `--no-js-runtimes` element of [0007](0007-live-acquisition-and-whisper.md); the rest of 0007 stands
- Review trigger: A yt-dlp change to default runtimes or EJS delivery, Node becoming a default, or Linux acquisition qualification

## Context

Decision 0007 passed a caller-supplied Deno executable explicitly and also sent `--no-js-runtimes`, intending to stop yt-dlp reaching for a JavaScript runtime — in particular Node.js, which the project rules exclude without explicit permission.

Checking yt-dlp's current documentation shows that flag does something different from what was intended. `--no-js-runtimes` "clear[s] JavaScript runtimes to enable, **including defaults**". Of the four supported runtimes, "only `deno` is enabled by default"; Node, QuickJS and Bun are already disabled by default, for the same security reason RoughCut cares about. yt-dlp finds Deno on `PATH` or beside `yt-dlp.exe`, and never downloads or installs a runtime.

An external runtime became necessary for full YouTube support in yt-dlp 2025.11.12 ([announcement](https://github.com/yt-dlp/yt-dlp/issues/15012)). It is not a hard failure: without one, "format availability will be limited, and severely so in some cases", and is "expected to worsen as time goes on". So clearing the default bought no protection that yt-dlp did not already provide, and silently cost format availability on every acquisition — including on a machine where Deno was installed and ready.

## Decision

Stop sending `--no-js-runtimes`. Let yt-dlp apply its own default, which is Deno only. Keep passing `--js-runtimes deno:<path>` when a caller supplies an explicit path, so a specific Deno binary can still be pinned for reproducibility.

Keep `--ignore-config` and `--no-remote-components`. The latter is the flag that actually prevents code being fetched at run time: yt-dlp can otherwise download EJS scripts from GitHub or npm, and official standalone builds already bundle `yt-dlp-ejs`, so blocking remote delivery costs nothing.

Do not enable Node, QuickJS or Bun. RoughCut never installs a runtime.

## Consequences

Acquisition behaves like plain yt-dlp on a machine that already has Deno, with no configuration, and reaches the full set of YouTube formats. Where no runtime is present, acquisition still succeeds with the reduced format set yt-dlp offers, rather than failing.

The runtime in use is now discovered rather than fixed, unless a path is supplied, so an acquisition is reproducible only to the extent that the machine's Deno is. `ROUGHCUT_DENO` and the CLI argument remain the way to pin it. Acquisition is still bounded, staged, playlist-free and configuration-free, and it never fetches JavaScript.

## Evidence

See the [acquisition/speech guide](../acquisition-and-speech.md) and the executable acquisition checks in `RoughCut.Tests`, which assert the default runtime is neither cleared nor overridden and that a supplied path is still pinned.
