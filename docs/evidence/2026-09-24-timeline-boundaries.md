# Draggable timeline boundaries evidence

- Date: 2026-09-24
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, CupriFace 0.26.1
- Scope: trimming a clip by dragging its edge in the timeline, and clips sized by their share of the edit

`75 passed; 0 failed` with `--media --cli --desktop --mcp`. Two checks are new.

## Clip boundary geometry moves one edge and stays inside the source

The mapping is a pure function, tested on its own. Dragging the start moves only the in point and dragging the end moves only the out point, so a drag is a trim of that clip rather than a ripple into its neighbours. Fractional pixel distances round away from zero. Neither edge may pass the other, leave the source, or leave a clip too short to see: the shortest a drag may produce is twenty milliseconds, or one tick in a time base too coarse for that. An unmeasurable distance leaves the clip alone rather than corrupting it, and a clip that does not start inside its source is refused outright.

## Dragging a clip boundary persists one revisioned trim

The window check is a real pointer drag, not a simulated call: the rendered handle is found by measuring the document, and press, move and release are dispatched at its measured position. Dragging the long clip's end left by a tenth of its rendered width shortens it by a tenth of its duration, within two ticks of rounding. Exactly one revision is written, the clip's other edge and its neighbour's interval are untouched, and undo restores the boundary.

The same check measures the row's shape: a clip lasting four times as long is rendered more than twenty pixels wider, so the timeline reads as the edit's shape and a drag moves a distance that matches what it changes.

## Two layout defects, found by looking

A snapshot was rendered and inspected at each step. The handles first measured ten pixels wide and **zero high** — an absolutely positioned child does not take its height from `top`/`bottom` in this engine — so the pointer never reached them and the drag could not start. Giving them a height fixed that, and then they sat on top of each clip's own labels, because an absolute child is offset from the content box here rather than the padding box: adding side padding moved the text and the handle together. The handles are now ordinary flex items beside the labels, which cannot overlap them by construction and take their height from the row.

## Limitations

The drag has not been performed by a person on a physical device; the harness dispatches the pointer events. Only the horizontal axis is exercised, at one clip size, on a two-clip timeline. A boundary drag trims one clip; it does not move a cut between two adjacent clips as a single operation, so closing a gap still takes two drags. Clips narrower than their 112-pixel minimum stop being proportional, and the drag then converts pixels using the clip's real rendered width, which is wider than its share.
