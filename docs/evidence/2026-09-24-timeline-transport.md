# Timeline transport evidence

- Date: 2026-09-24
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, CupriFace 0.26.1
- Scope: the review window's transport, which now reads the edit rather than the source copy behind it
- Closes: the limitation recorded in [decision 0019](../decisions/0019-approximated-preview-playback.md) and in the [interactive window evidence](2026-09-21-interactive-window.md) — "the player's transport spans the source rather than the timeline, so its position and duration do not correspond to the edit"

`73 passed; 0 failed` with `--media --cli --desktop --mcp`. One check is new.

## The transport reads the timeline, not the source copy behind it

The player decodes one copy of the whole source, so its own position is a source position. The transport now maps that back through the clip being played: for a timeline keeping one second out of each half of a four-second source, the duration reads 2 seconds rather than 4, and source second 3.5 reads as timeline second 1.5 — three quarters along the scrub bar. Pressing the track maps the other way, and clamps at both ends: a press before the track start seeks to 0, one past its end to the timeline's duration, and a zero-width track seeks nowhere rather than dividing by it. Formatting is checked at three scales, including past an hour and below zero.

Rendered over real media, the window shows `0:00.000 / 0:02.000` for that timeline and no longer contains the player's own control bar: `cupri-video-bar` is absent from the rendered document and the scrub track is present.

## The layout was measured, not assumed

Adding a row inside the preview card pushed the cards below it out of the window. The check measures the rendered boxes: the scrub track must sit inside the preview card, and the evidence card must end within the 800-pixel window. It failed on the first run at 808.6 pixels — the row's own height plus the export status line the previous slice added — and passes after the picture's minimum height came down to 180 pixels. The picture still takes every pixel the layout leaves it, so a larger window gives a larger picture.

A snapshot was rendered and looked at before and after, which is how the stretched buttons were found: the play and mute buttons had taken the row's spare width and squeezed the scrub track into the middle third.

## Limitations

The transport has not been dragged by a person, nor by the harness: a real press needs an open player, which needs native decoders and a prepared preview copy, so only the mapping, the formatting, the rendered markup and the layout are covered. Play, pause and mute are likewise wired but not exercised headlessly.

Replacing the player's own bar drops its fullscreen control; play, pause, seek and mute are reimplemented in timeline terms, fullscreen is not. Position is read at ten updates a second, so the readout can lag the picture by up to 100 ms, and it is only redrawn when the reading changes.
