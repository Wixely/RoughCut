# Foundation execution evidence

- Date: 2026-09-19
- Owner: Implementation agent
- Platform executed: Windows x64, Windows PowerShell 5.1
- SDK: .NET 10.0.300
- Media tools: FFmpeg and FFprobe `2026-04-01-git-eedf8f0165-full_build` (Gyan distribution)
- Review trigger: Contract, adapter, SDK, FFmpeg or publish changes

## Commands and observed results

| Command | Expected | Observed |
| --- | --- | --- |
| `dotnet build RoughCut.slnx` | Clean build | Passed, zero warnings/errors |
| `.\scripts\verify.ps1 -PublishAot` | Managed tests, NativeAOT publish, published CLI checks | Passed: 15 checks in each run; zero failures |
| `dotnet format RoughCut.slnx --verify-no-changes --no-restore` | No formatting changes | Passed after correcting initial whitespace issues |

The 15 checks cover exact rational comparisons, speaker/image/crop project round trips, reordered timeline mapping, invalid ranges/references/versions, unsafe relative paths, unknown/null JSON, stale and cancelled saves, competing writers, document limits, synthetic media generation, exact interframe pixels, half-open boundaries/final frame, nonzero origins, cancellation/timeouts/output limits, variable timestamps/gaps and CLI operations including overwrite/stale-save rejection. Core checks run in the managed harness; the second run invokes the actual NativeAOT CLI for its command checks.

## Fixtures and timing evidence

Fixture definitions are in `tests/RoughCut.Tests/Program.cs`. The harness uses FFmpeg `testsrc2=size=160x96:rate=10:duration=2`, encoded losslessly with libx264, `-qp 0 -g 20 -threads 1`, and no audio, in Matroska. It adds an offset variant using `-c copy -output_ts_offset 5`, and a variable-timestamp FFV1 variant using `setpts=if(lt(N\,3)\,PTS\,PTS+0.2/TB)` with `-fps_mode vfr`. All assets are synthetic and generated locally under ignored `artifacts/tests/`. Tool arguments are committed in the test source; hashes are calculated from each generated source and exposed in inspection/frame results rather than relying on encoder-version-independent binary hashes.

The 0.35-second request selects frame 3 at 0.3 seconds, whose PNG bytes match a fresh direct synthetic-source render. A 0.4-second boundary selects frame 4; 1.999999 seconds selects the final frame, and 2 seconds is rejected. The offset fixture selects the same source-relative frame despite its nonzero stream origin. In the variable-timestamp fixture, 0.55 seconds selects frame 3 at 0.5 seconds, and a request in its explicit timestamp gap is rejected. Source hashes remain unchanged. A running subprocess is cancelled at its configured timeout, and excessive output is rejected.

## Limits of this evidence

No audio/export joins, caption imports, MCP client/server, incoming image import, GUI, STT, diarization, Qwen TTS or Linux execution was tested: those features do not exist yet. Frame extraction scans from the beginning and is deliberately bounded; it is not a proven long-video or universal codec solution. The initial fixtures have no audio, rotation, non-square pixels or HDR. NativeAOT success covers this small dependency-free stack only. VS Code build/launch paths were verified by execution, but no interactive debugging session was performed. No remote, commit or publication was created.
