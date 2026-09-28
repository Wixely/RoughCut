# 0026: The window needs no agent

- Date: 2026-09-27
- Status: Accepted for the desktop review window
- Extends: [0014](0014-cupri-desktop-review.md) and [0019](0019-approximated-preview-playback.md); offers the choices recorded in [0022](0022-stream-copy-mux-export.md) and the rendition policy in the [acquisition guide](../acquisition-and-speech.md)

## Context

Everything RoughCut had learned about choosing a rendition, choosing an export format and finding where a cut can land was reachable only over MCP. A person who opened the window and pasted a video URL got a single **Fetch** button that silently picked a middle-bitrate rendition, downloaded it with no indication of progress beyond a sentence, and dropped them into an edit. Exporting produced one thing — an H.264/AAC MP4 — and the window could not be told otherwise.

And while an agent edited a project over MCP, the window showed whatever it had loaded, until somebody noticed and pressed **Reload**.

So a person needed an agent to do things the product already knew how to do, and two clients of the same project could not be open at once without one of them lying.

## Decision

The window does the whole job itself, using the same operations an agent calls.

**Choosing a rendition is a step, not a guess.** **Fetch** now asks the source what it offers and lists it — quality, codecs, frame rate, bitrate, stated size — largest first, with the download bound named. Nothing is downloaded until a row is clicked. A rendition that cannot fit the bound is still refused by the policy that already existed, before anything is written.

**Where the edit goes is shown before it is created.** The generated folder is offered in an editable field, named from the video's own title ([0025](0025-one-edit-one-folder.md)), with the platform's folder browser behind **Choose…**. The same field appears when creating an edit from a local video, whose copy into the folder is the same kind of progress-reporting work as a download.

**Progress comes from the tool, never from a timer.** yt-dlp is asked for machine-readable progress lines and FFmpeg for `out_time_us` against the planned duration; both reach the window through one streaming line callback on `ToolProcess`. A bar that moved on its own schedule would be a decoration; these move because something happened. The same numbers now fill in `ExportJob.ProgressPercent`, so an MCP caller polling a job sees real progress too, recorded at most once per whole percent because each checkpoint is a file write.

**Export offers what the exporters can produce.** The menu is built from `roughcut_list_export_formats`, so it cannot drift from them: the copy options first — naming the source's own video codec, `.mkv` always and `.mp4` where those codecs belong in one — then MP4, MKV, MOV and WebM, which re-encode. **A copy is the default**, because a cut-down usually wants the source's own packets and finishes in seconds; the window says plainly that its cuts move to the nearest keyframe and by how much, where a re-encode lands them exactly.

**The window follows the project.** A watcher on the project file notices an outside save, compares the revision on disk with the open one, and reloads only when they differ — so the window's own saves cause nothing, and an agent's edit appears without anyone pressing Reload. A change arriving while the person's own edit is in flight is retried rather than dropped. The watcher is a hint that something changed, never a description of what changed: the project file remains the truth.

## Consequences

- A person can open RoughCut, paste a URL, pick 720p, choose a folder, watch it download, cut, and export a copy — without an agent, and without a terminal.
- An agent and a person can work on the same project at once. The window will jump to the agent's revision; a selection made against the older revision is re-derived by the reload rather than preserved.
- Every new control is a thin shell over an operation that already exists headlessly, so MCP parity is kept by construction rather than by remembering.
- WebM is offered and is slow: VP9 at quality settings takes far longer than H.264 on the same timeline. The menu names the codecs so the choice is informed, but nothing warns about the time.
- The folder browser is platform code: the Windows shell browser, or zenity/kdialog on Linux. Where neither exists the field still accepts a typed path.

## Evidence

`docs/evidence/2026-09-27-desktop-acquisition-and-export.md`. The suite creates an edit from a local video into a named folder with progress and an untouched original, asserts the window offers both copy and re-encoded formats with a copy as the default, and proves the window follows an outside revision written by another writer while it is open.

## Review trigger

Before the window gains a control that only an agent can drive, or if following an outside change is ever observed to discard work a person was in the middle of.
