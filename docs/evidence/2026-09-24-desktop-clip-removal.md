# Desktop clip removal evidence

- Date: 2026-09-24
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, FFmpeg 2026-09-17-git-7070fe638e (gyan full build)
- Scope: `insert-clip` as the inverse of `remove`, and the review window's Remove control
- Decision: [0021](../decisions/0021-reversible-clip-removal.md)

`72 passed; 0 failed` with `--media --cli --desktop --mcp`. Two checks are new.

## Insert-clip restores a removed clip exactly, and refuses anything else

A three-clip timeline whose middle clip carries a crop, a `cover` fit and a `silence` audio policy is reduced by `remove` and rebuilt by `insert-clip`. The restored timeline compares equal to the original clip for clip, so the crop, the fit, the audio policy and the position all survive the round trip — the check compares the clip records themselves rather than their IDs. Removing the final clip and restoring it without a `beforeClipId` appends it back to the end.

Seven refusals are asserted, each leaving the timeline untouched: an ID that already exists, an interval past the asset duration, an empty interval, an image asset, an unknown asset, an unknown `beforeClipId`, and an audio policy that is neither `source` nor `silence`. An `at` field, which belongs to `split`, is refused by the existing rule that an edit may not carry fields unrelated to its action.

## Desktop clip removal persists with undo and redo

A three-clip project stands in for the real case — an advert between two retained sections. Selecting the middle clip and pressing Remove leaves `first, last`. Undo restores `first, advert, last` with the middle clip equal to the record captured before removal, in its original position. Redo removes it again, and the project on disk is at revision 4: one revision per edit, with no partial state.

Removing down to a single clip then refuses: the last clip cannot be removed, the timeline still holds one clip, and the rendered document hides the control rather than offering an action that would fail.

## Limitations

The window's Remove has not been clicked by a person on a physical device; the harness drives the session, and the control's presence and hiding are checked in a rendered document. Removal is exercised on a three-clip synthetic timeline, not on a real acquired project with many clips. `insert-clip` is proven as an inverse; it has not been exercised by an agent over MCP as a standalone editing action, though it reaches CLI and MCP through the same `apply_edits` path as every other action.
