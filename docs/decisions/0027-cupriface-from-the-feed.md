# 0027: Take CupriFace from its feed, at the version it is now

- Date: 2026-10-09
- Status: Accepted for the desktop review window
- Supersedes: The vendoring element of [0014](0014-cupri-desktop-review.md) and its 0.26.1 pin; the rest of 0014 — the shell choice, media resolution staying in `RoughCut.Application`, and no live-playback claim from that slice — stands
- Review: A CupriFace release that does carry a `### Breaking` section affecting this project, or publication to nuget.org

## Context

0014 pinned CupriFace 0.26.1 and kept the three `.nupkg` files in `vendor/nuget` so the desktop built without configuring a private feed. Its own review trigger named a CupriFace upgrade.

Fifteen minor releases later the pin had become the reason the window was missing work done upstream, and the vendoring had stopped buying what it was for. `packageSourceMapping` maps `*` to nuget.org, and a source the mapping does not name is excluded outright — so the project's `RestoreAdditionalProjectSources` folder was never consulted, and 0.26.1 was resolving out of the machine's global package cache. The vendored files were being carried in the repository and read by nobody. A contributor with a cold cache would have been refused, which is the case vendoring existed to serve.

CupriFace is published to GitHub Packages and not to nuget.org. The estate already restores one package from an authenticated feed — `DnaX.MCPFab`, under the same source name — so the credential path, the CI injection and the "never commit a token" rule are established rather than new.

## Decision

Restore `CupriFace`, `CupriFace.Media` and `CupriFace.Shell` 0.41.0 from the Wixely GitHub Packages feed, named for the `CupriFace*` pattern in `packageSourceMapping`, and delete `vendor/nuget`. Keep the exact-version pins: `[0.41.0]`, so an upgrade stays a recorded decision rather than a restore.

Verify the feed against the published release rather than trusting it: the SHA-256 of each restored package was compared with its GitHub release asset before the vendored copies were removed, and all three match.

Gate the window's markup on `CupriDoctor` as part of the desktop suite, over a populated review session. The engine ignores a property or an element it does not implement instead of throwing, so a mistake in the markup is indistinguishable from an unfinished layout; the checker is the only thing that reads that silence back. Errors and warnings fail; `Info` is advisory.

## Consequences

Building the desktop window now needs feed credentials, which the libraries and the command line still do not — the boundary moved, and [dependencies](../dependencies.md) and [desktop](../desktop.md) say so. The repository stops carrying 4 MB of binaries it was not reading.

Three CSS declarations the engine had been discarding are fixed, two of them a real defect: the transcript and recent-project lists were written `overflow-y:auto`, which is not a property the engine styles, so neither list scrolled. Future markup mistakes of that shape fail the gate instead of looking like unfinished work.

What the fifteen releases added is not adopted here. This decision takes the upgrade for the UI that exists; new CupriFace capabilities are unexamined and remain available.

## Evidence

See [2026-10-09 CupriFace 0.41.0 evidence](../evidence/2026-10-09-cupriface-0.41.0.md).
