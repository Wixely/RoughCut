# 0021: Reversible clip removal

- Date: 2026-09-24
- Status: Accepted for the desktop timeline-editing slice
- Extends: [0017](0017-reversible-timeline-editing.md), which added `set-range` for the same reason

## Context

RoughCut exists to cut unwanted material out of a video, and until now the review window could not do it. It could trim, split, reorder and crop, but not remove a clip. Cutting an advert meant splitting around it and then leaving it in place, or dropping to the CLI.

The reason was deliberate and recorded in the desktop guide: `remove` has existed in the edit engine since RC-02, but nothing could put a video clip back. Every desktop control saves one revision-checked edit whose exact inverse the undo stack replays, and a removal had no inverse, so the window refused to offer it. That is the same constraint 0017 resolved for trim, where `trim` can only narrow and therefore cannot express its own inverse: the answer there was `set-range`, an action that states the interval it wants rather than the change it makes.

## Decision

Add an `insert-clip` edit action that is the inverse of `remove`. It names the clip to restore in full — `clipId`, `assetId`, `in`, `out`, and the optional `crop`, `fit`, `audio` and `beforeClipId` — so the restored clip is the removed one, not an approximation of it. `beforeClipId` places it; omitting it appends. `EditOperation` gains an `audio` field, which only this action accepts, because a video clip may legitimately carry `silence` and a restoration that quietly changed it would not be a restoration.

It restores video clips only. Image holds keep `insert-image`, which is not an inverse of anything: it creates new material with a duration, where this replaces material that was already there.

It is not a general insertion. The interval must be nonempty and inside the named video source, the clip ID must be new, and a named `beforeClipId` must exist. It is deliberately usable for more than undo — an agent can restore a clip it removed — but it cannot invent material the source does not contain.

The window then offers **Remove** on the selected clip, hidden while the timeline has only one clip: an empty timeline has nothing to preview, and nothing in the window could add a clip back to it. The Core engine still permits `remove` to empty a timeline, because CLI and MCP callers can rebuild one and their behaviour is unchanged.

## Consequences

- The window can now perform the operation the product is for, end to end: open a URL, find the unwanted section, cut it, export the MP4.
- Removal is as reversible as every other desktop edit; undo restores the exact clip in its exact place, and redo removes it again, one revision each.
- The edit vocabulary now has two insertion actions that read similarly. `insert-clip` restores video material that exists in a source; `insert-image` adds a timed still. The distinction is enforced: each refuses the other's asset kind.
- An `audio` field exists on `EditOperation` that only one action accepts. Every other action rejects it, as it rejects any field unrelated to itself.

## Evidence

`docs/evidence/2026-09-24-desktop-clip-removal.md`. The suite proves an exact `remove`/`insert-clip` round trip at the Core level, including crop, fit, audio policy and position, and rejects insertion that would duplicate an ID, leave the source, empty the interval, name an image asset or an unknown asset, place before an unknown clip or set an unknown audio policy. The desktop check removes the middle clip of three, undoes it back to the identical clip in the identical place, redoes it, and confirms the last clip cannot be removed and that the control is hidden when it would be.

## Review trigger

Before adding any further destructive desktop edit, or before allowing the window to empty a timeline.
