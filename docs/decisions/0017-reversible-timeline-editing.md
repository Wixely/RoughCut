# 0017: Add a set-range edit action so interactive trims are reversible

- Date: 2026-09-20
- Status: Accepted for the bounded desktop timeline-editing slice
- Review: When a timeline edit action is added or removed, or when desktop undo history becomes durable

## Context

RC-06 needed revision-safe clip trim, split and reorder controls in the desktop review host. Every desktop edit so far — speaker labels and crops — persists through the shared revision-checked edit boundary and is reversible through an exact inverse operation, so desktop, CLI and MCP callers keep identical revision semantics.

The existing `trim` action deliberately refuses to widen a clip: it requires the new interval to sit inside the current one. That guard is valuable for a submitted batch, where silently extending a clip past what the author saw would be a correctness risk. It also means `trim` has no inverse expressible as an edit operation, and that an interactive editor cannot let a reviewer pull a boundary back out after dragging it in. Neither can `split` be reversed, because no action merges two clips.

## Decision

Add a `set-range` edit action that replaces a clip's retained source interval with any nonempty interval inside its asset, keeping image holds anchored at zero. Keep `trim` unchanged and narrowing-only, so an unattended batch still cannot extend a clip.

The desktop trim control emits `set-range`, since the reviewer is looking at the source frame being chosen. Desktop undo replays the exact prior interval with `set-range`; undoing a split removes the created clip and restores the original interval in one transactional batch; undoing a reorder replays the prior order. Clip removal stays out of the desktop surface, because reinserting a removed video clip is not an available edit action and the undo could not be exact.

## Consequences

Interactive trimming is reversible without a separate history format, and desktop undo/redo continues to cost exactly one revision per step. CLI and MCP callers gain `set-range` for free through the existing `EditOperation` array, with no new tool or command surface. Batch authors keep a narrowing-only `trim` when they want that guarantee.

Desktop undo history remains session-local and is still cleared on reload; it is not persisted in project JSON. A timeline edit invalidates the playback proxy, and a selection whose source time is no longer retained falls back to the first retained frame. Merging arbitrary adjacent clips, dragging timeline boundaries and removing clips from the desktop remain unimplemented.

## Evidence

See [2026-09-20 desktop timeline-editing evidence](../evidence/2026-09-20-desktop-timeline-editing.md).
