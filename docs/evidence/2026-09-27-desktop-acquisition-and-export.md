# Acquiring, organising and exporting from the window

- Date: 2026-09-27
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, CupriFace 0.26.1, FFmpeg/FFprobe 8.0, yt-dlp 2026.09.08
- Scope: the desktop launcher's rendition choice, one-folder edits, progress reporting, the export format menu, and following an outside edit
- Records: [decision 0025](../decisions/0025-one-edit-one-folder.md), [0026](../decisions/0026-the-window-needs-no-agent.md)

`89 passed; 0 failed` with `--media --cli --desktop --mcp`. Eight checks are new, and the live runs below are by hand.

## Every offered export format is produced and probed

The format menu claims a container and two codecs, so each is produced and read back: MP4 and MKV and MOV carrying H.264 with AAC, WebM carrying VP9 with Opus. For each target the check asserts the preflight plan describes the target it was given, the bundle holds `video` with the right extension, FFprobe reports both promised codec names, and the delivered length matches the second the timeline claims. An unknown format name is refused by name rather than attempted.

That is why the target list is a closed set: a target is a claim about what the file carries, and each one here is a claim the suite tests.

## What a caller may offer comes from RoughCut

`roughcut_list_export_formats` returns the copy options first — labelled with the source's own video codec, `.mkv` always and `.mp4` only where those codecs belong in one — then the four re-encoded targets. The window builds its menu from that list, so a menu entry cannot exist for something the exporters cannot produce, and the default is the first copy option.

## An edit is one folder

The naming reduction is checked directly: `Fixture: Part 2 <HD> | 4K?` becomes `Fixture Part 2 HD 4K`, trailing dots and runs of spaces go, an empty or punctuation-only title falls back to `project`, `nul` becomes `nul-project`, and a 200-character title is cut to 80. An existing `Fixture title` yields `Fixture title-2` rather than being written into, and exhausting the search is an error rather than an overwrite.

Copying into the folder is checked on a three-megabyte file: the copy equals its source byte for byte, progress rises from below half to exactly one, an existing destination is refused, and a copy cancelled part-way leaves neither the file nor its `.partial` fragment.

In the window, an edit created from a local video puts `project.json` and the video's copy in the chosen folder, references the video by a portable relative path, reports progress reaching completion, and leaves the person's own file in place. A second edit into the same folder is refused.

## Progress comes from the tools

The download progress path is exercised without a network: the acquisition fixture emits the same lines the real tool writes for the template RoughCut asks for, and the check asserts the arguments request machine-readable progress and that the fractions 0, 0.475 and 1 reach the caller in order. FFmpeg's `out_time_us` drives export progress through the same streaming callback, and the job layer maps it into `ProgressPercent` between starting and the checks that follow, so a reader never sees 100% while validation is still to come.

## The window follows an outside edit

A project is opened in the window; another writer then saves the next revision to the same file, which is exactly what an agent's `roughcut_apply_edits` looks like from the window's side. The check pumps the window as a live one would and asserts the header shows the new revision without anyone pressing Reload.

## The whole flow, on a live source

Run against a real 13.7-minute source, through the same methods the window's controls call:

- The picker was rendered from the live listing with `snapshot --url`: 42 renditions, the title and duration, the 512 MiB bound, and the folder named from the title. The first render showed the list **overflowing the launcher card** and colliding with the buttons beneath it — the card had a fixed height and no clipping, and the `hidden` class was only defined for specific selectors, so putting the other entry points away did nothing. Both are fixed: the card clips, the list scrolls inside it, and the other ways into a project step aside while a quality is being chosen.
- Picking the smallest rendition (`160`, 144p at 39 kbps) failed outright: the policy refuses a picture-only identifier, while the window's own row text promises "sound added from the best audio". The window now pairs the rendition with the source's best audio before asking, so it keeps that promise; an API caller still gets the refusal that explains itself.
- With that fixed: `160+249` downloaded in 14 seconds with 17 progress steps (two streams, each running to 100%), landing `source.mkv`, `project.json`, `acquisition.json` and `source.info.json` in one folder named after the video.
- The export menu offered `Original: h264 (.mkv)`, `Original: h264 (.mp4)`, and the four re-encoded targets, with the copy first.
- Trimming ten seconds off the head and exporting the default copy took 1.4 seconds and published `project-export/video.mkv` beside the project, reporting that cuts moved by up to 0.125 s to reach a keyframe.

## What an actual window showed that a snapshot did not

Opening the real window and fetching a URL produced the defect this work was closest to missing: with a rendition chosen and the download running, the progress bar, its label and **Cancel** were drawn **on top of the rendition rows**. The snapshot had not shown it because the progress row is not visible while a list is merely being read.

The cause is the shell's layout: it lays out every row a list contains whatever height the container is given, so a forty-four-rendition list claimed the whole card and everything placed after it landed on the rows. `overflow-y:auto` and a fixed height do not change that — the second attempt at a fix still overlapped, which the check caught.

What actually fixes it is building fewer rows: one per picture size, the most compatible codec at that size, at most eight, in compact single-line rows the card has room for. The list is also put away once a rendition is chosen, since its job is done.

The check that guards it renders 42 synthetic renditions across ten sizes and three codecs with the progress row showing, and asserts no two controls in the card overlap. It was written twice: the first version hid the list, which is the state *after* the fix, and so passed against the broken layout. The version kept reproduces what the person saw, and fails against it.

Two things this showed that are not defects but are worth knowing: a short export reports one progress step, because FFmpeg finishes before it emits an intermediate position; and this source offered no usable English subtitles through the chosen rendition, so the project has no caption track — reported rather than failed.

A source fetched as separate video and audio runs the bar from nothing to full once per stream, and the few seconds after the last 100% — fingerprinting the download and reading its shape — are not reported at all, so the bar sits full while they finish.

## The window froze at 100%, and why

Fetching a 1080p60 rendition of a fourteen-minute video reached 100% and then did nothing. The folder left behind said where it stopped: `source.mkv`, its subtitles and `acquisition.json` were all published, and `project.json` was not — so the download had finished and the step after it was still running.

That step was `MediaReader.InspectAsync`, which every path used to learn a source's codec, size, time base and length. It runs `ffprobe -show_frames` across the whole file, walking every frame: **2 minutes 17 seconds** on this 236 MiB source, during which the window has nothing to report and looks frozen. The same walk was also paid when preparing playback, and on every open of the export format menu.

A project needs the container's own facts, not a frame index, so `MediaReader.ProbeAsync` reads them from the container: the same codec, dimensions, time base and start, with the length taken from the stream where it states one and from the container's duration where it does not — Matroska usually does not. Creating a project from the same file now takes **2.7 seconds**, of which nearly all is the SHA-256 of 236 MiB.

`InspectAsync` remains, and remains the right call for work that must name a particular frame: a frame-exact render, and the strict export path's validation. The suite asserts the two agree on everything but the frame count, whose absence the probe reports as zero rather than inventing.

## Limitations

No check in the suite downloads from the internet; the live run above was driven by hand.

The window's own buttons were never pressed. This shell dispatches pointer events to RoughCut's drag regions but registers no clickable region for its `cupri-button` elements, so a headless check cannot click **Fetch**, a rendition row, **Choose…**, **Cancel** or **Format…**. Those handlers are wired by inspection only, and pressing them remains a physical-device check.

The folder browser is native platform code and is not exercised headlessly — only the generated and typed paths are. Nothing measures how long a WebM export takes; VP9 at these settings is markedly slower than H.264 and the menu says nothing about that.

Following an outside change reloads the project, which re-derives the selection rather than preserving it. A person mid-selection when an agent saves will see their selection reset, which no check asserts either way.
