# Desktop review

- Added: 2026-09-19
- Updated: 2026-09-27 (choosing a rendition and a folder, progress, export formats, following outside edits)
- Owner: Implementation agent
- Review: When CupriFace changes, the proxy format changes or Linux acceptance begins

## Opening and creating projects

`roughcut-desktop` is one of the two artefacts RoughCut ships, beside the MCP server an agent launches; both are shells over the same library, which is why the two can work on one project at once. See [packaging](packaging.md).

Started with no arguments, RoughCut opens its window on the project launcher: the projects opened most recently on this computer, a video-URL field, **New from a local video…**, **Open a project…**, a drop target, and a path field. Choosing one opens it; the window then behaves exactly as if the path had been passed on the command line, and **Open another** returns to the launcher.

Pasting a URL and pressing **Fetch** asks the source what it offers and lists it — nothing is downloaded yet. A source offers the same picture size several times over in different codecs — 44 renditions for an ordinary video — which is a distinction nobody choosing a quality wants to make, so the card shows **one row per size**, largest first: H.264 where a size offers it, because it plays everywhere and can be copied into an MP4, then AV1, then VP9. Each row carries its size and codec by the name the codec is known by, its format identifier, extension, frame rate and bitrate, and the size the source states, with the download bound named above them. At most eight sizes are listed, and the hint says how many renditions they were chosen from. A source usually offers its best pictures without sound, so a row that is picture-only says "sound added from the best audio" and the window requests that pairing when it is clicked. Clicking a row downloads that rendition *and its subtitles* through the bounded yt-dlp policy, creates a project for it and selects the best caption track — the same `CreateProjectFromUrlAsync` the CLI `create-url` command and the `roughcut_create_project_from_url` MCP tool call. A person therefore picks the quality themselves, which until now only an agent could do ([decision 0026](decisions/0026-the-window-needs-no-agent.md)).

The yt-dlp arguments stay RoughCut's: `--write-subs --write-auto-subs --sub-langs en,-live_chat --sub-format srt/best --convert-subs srt`, plus `--ignore-config`, `--no-playlist` and the size bounds described in the [acquisition guide](acquisition-and-speech.md). No caller has to remember them, and none can drift. A rendition too large for the 512 MiB bound is refused before anything is written rather than aborting part-way.

Each edit lands in **one folder** holding the media, the project file and every asset it generates ([decision 0025](decisions/0025-one-edit-one-folder.md)). The folder is offered before the download starts, named from the video's own title under `%USERPROFILE%\Videos\RoughCut` — which `ROUGHCUT_PROJECTS` overrides — in a field a person can edit, with the platform's folder browser behind **Choose…**. A name that a filesystem would refuse is reduced to one it accepts, and an existing folder is never written into: `Fixture title` becomes `Fixture title-2`.

A download or a copy can be stopped with **Cancel** beside the progress bar, and stopping one publishes nothing: the acquisition stages into its own directory and a copy is written under a temporary name, so both disappear and the launcher returns exactly as it was.

A download shows a real progress bar. yt-dlp is asked for machine-readable progress and the bar follows it, so it moves because bytes arrived rather than on a timer; a source fetched as separate video and audio runs to 100% once per stream, and the merge that follows reports nothing. Copying a local video into its folder reports progress the same way.

The desktop resolves yt-dlp, FFmpeg, FFprobe and Deno through the shared order in the [development guide](development.md), so they need not be on `PATH`. Deno needs no configuration when it is on `PATH` or beside `yt-dlp.exe`, because yt-dlp enables it by default and RoughCut no longer clears that; `ROUGHCUT_DENO` only pins a specific binary. A source with no usable subtitles still produces a valid project and reports why.

**New from a local video…** picks a video and creates an edit in a folder of its own: the video is **copied** into it beside `project.json`, with a single clip covering the whole source. The folder is named after the video under the projects root, and the person's own file is left exactly where it was — the copy is what the project fingerprints, so a later change to the original cannot invalidate the edit. Copying a large video takes a moment and shows progress.

The older behaviour, a project written beside the video, remains available through the CLI `create` command for media already sitting where the edit should live.

The drop target and the path field accept either kind: a `.json` path opens an existing project, and anything else is treated as the video for a new one. That keeps creation available where no native picker exists.

**Open a project…** opens the real operating-system file picker. CupriFace 0.26.1 has no dialog of its own and its shell ships SDL2, which predates `SDL_ShowOpenFileDialog`, so RoughCut calls the platform directly: `GetOpenFileNameW` from `comdlg32.dll` on Windows, and `zenity` or `kdialog` on Linux. The dialog runs on its own single-threaded-apartment thread, so the window keeps painting while it is open, and it is owned by the RoughCut window so it stays in front. Both buttons are hidden when no picker is available, leaving drop and the path field. `GetOpenFileName` moves the process working directory as the person browses — `OFN_NOCHANGEDIR` is documented as ineffective for it — so RoughCut restores the working directory when the dialog closes; every workspace-relative path depends on that.

`roughcut-desktop snapshot --url <url> <out.png>` renders the picker holding a real source's renditions. It is the only way the picker's layout is checked without a device: this shell dispatches pointer events to RoughCut's own drag regions, but not to the shell's buttons, so no headless check can press **Fetch** itself.

A project that cannot be opened leaves the launcher visible with the reason, rather than dropping into an empty workspace or closing. A validation failure names the offending fields instead of reporting only that validation failed, unreadable JSON says so, and a missing file says that. A failure while building the review view is reported in place of the view. Anything that still escapes — including on a background thread or inside the window loop — is written to `%LOCALAPPDATA%\RoughCut\crash.log` with its stack before the process ends. Setting `ROUGHCUT_TRACE=1` records every exception as it is thrown, which is what a failure the shell catches and reduces to a bare message needs.

Background work never changes the view directly. CupriFace rebuilds the document when it refreshes, so a work thread that changed the model and refreshed could leave the render thread reading a half-built document — reported as `Document has no <body>`. Work threads queue their view changes and the render thread applies them.

The launcher list is per-user window state, not project data. It is stored outside the repository at `%LOCALAPPDATA%\RoughCut\recent-projects.json`, holds at most ten entries most-recent-first, and drops entries whose file no longer exists. A damaged or unreadable history is ignored rather than allowed to stop the window opening. `ROUGHCUT_RECENT_PROJECTS` overrides the file, which is how tests and headless renders stay deterministic.

## Reviewing

The RC-06 Windows desktop review surface uses CupriFace 0.26.1. It reads the same portable JSON project as the CLI and MCP host. Selecting a transcript or editorial-evidence row resolves that source time through the current timeline revision, asks the shared application layer for the exact rendered PNG, seeks the playback proxy to the resolved timeline time, and displays its source/timeline timing and active crop description.

### Preview playback

**No video is rendered while editing.** RoughCut makes one preview copy of each source — a VP9/Opus WebM scaled to at most 720 lines, because the player decodes nothing else — keyed by the source fingerprint and cached under the ignored `.roughcut-preview` directory. The timeline is then approximated over that copy: selecting a row seeks to the matching source position, playback jumps at clip boundaries so cuts and reordering are visible, and a crop is shown by scaling the picture inside a clipped frame. Editing changes which parts of the copy play, never the copy itself, so a trim, split, reorder or crop costs nothing to preview. See [decision 0019](decisions/0019-approximated-preview-playback.md).

This is deliberately an approximation, and it is labelled as one in the status line. Boundary jumps land on the decoder's nearest frame rather than exactly on the cut, audio is not gapless across a jump, and the copy is re-encoded, so it is not evidence of what export produces. Playing again after the timeline finishes starts it over. **A cropped clip plays cropped.** The preview copy is the whole source frame, so the crop is done by the view: the visible box takes the crop's shape, and while frames are running the picture inside it is enlarged and offset so the cropped region fills it. Both boxes come from one scale, so the picture keeps the source's aspect ratio rather than being stretched to fit, which is what an earlier attempt got wrong. Paused, nothing is enlarged: the poster is the exact rendered frame, already cropped, so both states frame the same region. An exact render already contains cropped pixels and is left alone. The box is computed from the preview's measured size, so it follows the window, and it falls back to the whole frame before the view has been laid out. See the [cropped playback evidence](evidence/2026-09-24-cropped-playback.md). It works for any source FFmpeg can decode, including the AV1/Opus material a URL fetch produces, which the validated export matrix rejects outright.

### Transport

The controls under the picture are RoughCut's, not the player's, and they read the **edit**: position and duration are timeline time, and the scrub bar spans the retained material rather than the whole source. The player is decoding one copy of the source, so its own position is mapped back through the clip that is playing; when an exact render is loaded the player already is the timeline and the position is used as it stands. Pressing or dragging the track seeks in timeline seconds, which means a press can only land on material the edit keeps — there is nowhere on the bar that corresponds to removed material. Play, pause and mute sit beside it, and pressing play on a finished timeline starts it over. See the [transport evidence](evidence/2026-09-24-timeline-transport.md).

This replaces the player's own control bar, which showed the source's position and duration and was the first thing to confuse a person reviewing an edit. The cost is its fullscreen control, which is not reimplemented. The readout updates ten times a second and only when it changes, so a still picture costs nothing to display.

**Render exact preview** builds the validated export-backed proxy on request: the VP9/Opus WebM including accepted cuts, reordering, crop rendering, inserted images and applied voice replacements, keyed by the exact project JSON hash and revision and capped at 128 MiB. That is the only place the desktop renders video, and it is an explicit action rather than a side effect of editing. It inherits the export gate below, so it refuses sources outside the validated matrix. CupriFace.Media supplies native VP9/Opus decoding, an audio-clocked player and play, pause, mute, seek and fullscreen controls.

### While an agent is working

The window follows the project file. When something else saves a new revision — an agent applying edits over MCP, or the CLI — the window notices, compares the revision on disk with the one it has open, and reloads only if they differ, so its own saves cause nothing. An agent's edit therefore appears in the timeline, the clip list and the header's revision without anyone pressing **Reload**, which remains for a manual refresh.

A change that arrives while the person's own edit is being saved is retried rather than dropped. Reloading re-derives the selection rather than preserving it, so a selection made against the older revision will reset.

### Exporting

**Format…** lists what this project can be exported as, and the button beside it exports in the chosen format into a new folder beside the project — `project.json` produces `project-export/`, then `project-export-2` — never overwriting an earlier one. The list comes from RoughCut rather than from the window, so it cannot offer something the exporters cannot produce:

- **Original: `<codec>` (.mkv)** and, where the source codecs belong in an MP4, **(.mp4)** — a [copy](export.md#mux) that keeps the source's own packets and finishes in seconds. This is the **default**, because a cut-down usually wants exactly the material it started with. Each segment's start moves to the nearest keyframe behind it; the window reports how far the worst cut moved.
- **MP4**, **MKV** and **MOV** carrying H.264 and AAC, and **WebM** carrying VP9 and Opus — a [delivery](export.md#delivery) re-encode that lands every cut exactly and renders the ordinary acquired media the validated export matrix refuses, including the AV1/Opus material a URL fetch produces. WebM is markedly slower to encode than the H.264 targets.

Exporting shows a progress bar driven by FFmpeg's own reported position.

A delivery file is a re-encode, not a copy of the source: it delivers the cuts, ordering, crop and retimed captions at the timeline's length, and the bundle's `delivery.json` says exactly that. Preflight runs first and starts no process, so a timeline delivery cannot render — a timed image, an applied voice replacement — is refused immediately with its reason, before an encode begins. A copy makes the opposite trade and its report states every boundary it moved.

The button reports the outcome under the preview: the folder, the size, the delivered length and the revision it came from. While an export is running the same button cancels it, and a cancelled export publishes nothing. An export never changes the project, and editing during one is allowed: the export renders the revision it started from, which its report names.

The timeline card shows each clip at its share of the edit's length, with a handle at either end. Dragging a handle trims that boundary: the clip's own rendered width sets the scale, so a tenth of the way along a clip is a tenth of its duration whatever the layout does to a very short one. Only the dragged edge moves — the other end, and every other clip, keeps its source interval — and the pending interval is shown while dragging, with one revision-checked `set-range` saved on release and reversed exactly by undo. A drag cannot pass the other edge, leave the source, or leave a clip shorter than twenty milliseconds. See the [boundary evidence](evidence/2026-09-24-timeline-boundaries.md).

The card also edits the selected clip by number. It names the clip, its position in the timeline and its retained source interval, then offers integer IN/OUT fields with **Apply trim**, plus **Split**, **Remove**, **Earlier** and **Later**. Apply trim sets the retained interval through one `set-range` operation, so it both shortens a clip and restores source material a previous trim dropped, bounded by the asset duration. Split divides the clip at the currently selected source frame and names the new clip after it, for example `middle` and `middle-2`. Earlier and Later exchange the clip with its neighbour through one `reorder` operation. Each control saves exactly one revision-checked edit; undo and redo replay the exact inverse, joining a split back into one clip in a single transactional batch. Controls that the current selection cannot apply are hidden rather than offered: Split appears only when the selected source frame lies strictly inside the clip, the move buttons only when a neighbour exists in that direction, and Remove only while another clip remains. Remove cuts the selected clip out of the timeline, which is how unwanted material actually leaves an edit. Its undo is an `insert-clip` operation restoring that clip's exact ID, interval, crop, fit, audio policy and position, so removal is as reversible as everything else here; see [decision 0021](decisions/0021-reversible-clip-removal.md). Remove is hidden for the only clip on a timeline, because an empty timeline has nothing to preview and nothing in the window could put a clip back into it. See [decision 0017](decisions/0017-reversible-timeline-editing.md) and the [timeline-editing evidence](evidence/2026-09-20-desktop-timeline-editing.md).

The review panel shows speaker labels and overlap/assignment state. Renaming a speaker uses the existing revision-checked speaker edit API. The crop editor shows an uncropped source frame, a proportional crop rectangle, four corner handles and integer X/Y/width/height controls for the selected video clip. Drag the rectangle to move it or a corner to resize it; live values are clamped to source pixels, and release persists one transactional crop edit. Cancel restores the starting rectangle. Apply and Full frame use the same edit API, while the numeric controls remain the precise keyboard-accessible path. Undo and redo persist inverse/forward speaker or crop operations, so project revision semantics remain consistent across desktop, CLI and MCP callers. A crop refreshes the exact rendered frame and invalidates/rebuilds the playback proxy. Reload clears local undo history and refreshes the selected frame from the saved revision. A project that cannot be opened leaves the launcher visible with the reason — a validation failure names the offending fields rather than reporting only that validation failed — and a failure while building the view is reported instead of closing the window. Anything that still escapes is written to `%LOCALAPPDATA%RoughCutash.log` with the reason. CupriFace text fields keep an intrinsic width and overflow a narrower grid cell rather than shrinking, so panel controls carry explicit border-box widths; a rendered check asserts nothing in the review panel escapes its bounds.

Run the window from Windows PowerShell:

```powershell
dotnet run --project src/RoughCut.Desktop
dotnet run --project src/RoughCut.Desktop -- review artifacts/demo/project.json
```

For a timeline whose strict export is currently unsupported, a known local WebM review render can be supplied explicitly as the third argument. The desktop labels it as a pre-rendered review proxy and does not claim it as a validated RoughCut export:

```powershell
dotnet run --project src/RoughCut.Desktop -- review artifacts/demo/project.json artifacts/demo/review.webm
```

A supplied render stays the session's playback. Editing the timeline does not discard it and does not attempt a validated rebuild that would fail for the very source it was supplied for; instead the status line reports that it does not show edits since the revision it was supplied at. Its positions no longer match the edited timeline, so seeking to a transcript row becomes approximate.

The **▶ RoughCut** VS Code launch starts the app with no arguments, so it opens on the launcher; the two **RoughCut desktop: open a project** launches prompt for a path and an optional pre-rendered preview. A deterministic headless render is available for testing, with the project path omitted to render the launcher itself:

```powershell
dotnet run --project src/RoughCut.Desktop -- snapshot artifacts/demo/project.json artifacts/demo/review.png
dotnet run --project src/RoughCut.Desktop -- snapshot artifacts/demo/launcher.png
```

The output path must not exist. FFmpeg and FFprobe must be available through the same configuration used by the application layer.

**Render exact preview** inherits the current export gate: one active video/audio source, no more than 32 clips, no more than 60 seconds and the documented frame/sample alignment and asset rules. Unsupported projects retain the exact-frame poster and show the failure instead of silently approximating the timeline. Frame requests are serialized off the event path; a newer selection cancels the older request and only a fully decoded result is committed. Revision-changing commands are also serialized, while proxy preparation is independently cancellable and discarded when its project revision becomes stale. The native player still loads the complete bounded proxy into memory. A timeline edit drops any exact render, since it is revision-bound, but never rebuilds the preview copy; a selection whose source time is no longer retained falls back to the first retained frame. The transcript is capped at 500 visible rows, the timeline at 200 clips and evidence at 100 proposals.

Windows headless rendering, dummy-device A/V timing and a framework-dependent Windows x64 publish have executed. The two-second published probe decoded 20 frames with 0.0 ms measured drift growth and zero underruns. An interactive window run has now happened: the window opened its launcher, opened an acquired AV1/Opus project and played the approximated preview, recorded in the [interactive window evidence](evidence/2026-09-21-interactive-window.md). Audio output was not assessed, so physical audio-device acceptance and A/V sync remain unverified, and Linux execution has not happened.

CupriFace and CupriFace.Shell release packages are stored under `vendor/nuget` with hashes and provenance. They are MIT-licensed public release artifacts. RoughCut does not redistribute CupriFace native media codecs in this slice; preview pixels come from the already validated RoughCut FFmpeg path.

Next owner/action: **Implementation agent: exercise the launcher and review window on a physical device — live pointer editing, audio-device playback and Linux execution — and measure representative speaker/overlap quality when fixtures arrive.**
