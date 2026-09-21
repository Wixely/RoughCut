# Interactive window evidence

- Date: 2026-09-21
- Platform: Windows 10 Enterprise, .NET SDK 10.0.300
- Scope: RC-06 first interactive run of the review window on a physical device
- Observed by: Wixely, reported to the implementation agent. The agent did not watch the screen; everything below is as reported, plus the captured output and the fixes it led to.

The window was started with no arguments, opened its project launcher, and opened a project acquired from a URL — AV1/Opus in Matroska, the material the validated export matrix rejects. The approximated preview played that project's video. This is the first time the review surface has been driven by a person rather than a headless render, and it closes the interactive-window half of the RC-06 gate.

It took three attempts, and each failure was a real defect now fixed.

## What failed, and what it was

**The window closed a few seconds after opening a project**, once also opening a second window first. The captured output was `[CupriFace] GPU unavailable (InvalidOperationException: Document has no <body>.); using the SDL software window.` followed by `Document has no <body>.` The GPU window failed with that error, the shell fell back to an SDL software window — the second window — and the same failure later ended the process.

`CupriDocument.Refresh` rebuilds the document. The launcher path changed the model and refreshed from work-thread completion callbacks while the render thread was reading that document, so the renderer could observe it part-built. Work threads now queue their view changes and the render thread applies them. A harness that races a render loop against opening a project does not reproduce the failure with or without that change, so the race remains the best-supported explanation rather than a proven one — but the window has not closed since.

**Nothing was recorded when it closed.** The shell caught the failure and printed only its message, so neither the unhandled-exception handler nor the log saw it. The window loop is now wrapped so a failure there is recorded with its stack, and `ROUGHCUT_TRACE=1` records every exception as it is thrown.

**Playing again after pausing was difficult.** The project's timeline retains 1.893 s of an 18.934 s source. Playback reached the end of the clip and paused, but the finished state was never cleared, so pressing play stopped again on the next frame. Playing now restarts the timeline, and selecting a row clears the state.

## Limitations

Audio output was not assessed, so physical audio-device acceptance and A/V sync remain unverified; the only drift measurement is still the headless dummy-device probe. Linux has not been run. The player's transport spans the source rather than the timeline, so its position and duration do not correspond to the edit. Only one project, one source and one clip were exercised.
