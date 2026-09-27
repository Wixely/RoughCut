# Cropped playback evidence

- Date: 2026-09-24
- Platform: Windows 10 Enterprise, .NET SDK 10.0.302, CupriFace 0.26.1
- Scope: showing a clip's crop on the moving picture, not only on the exact still
- Closes: the limitation recorded in the [desktop guide](../desktop.md) and [decision 0019](../decisions/0019-approximated-preview-playback.md) — "crop is not applied to the moving picture"

`76 passed; 0 failed` with `--media --cli --desktop --mcp`. One check is new.

## A cropped clip plays cropped, without stretching the picture

The preview copy is the whole source frame, so the crop has to be done by the view. One scale drives two boxes: the visible box takes the crop's shape, and while frames are running the picture inside it is enlarged and offset so the cropped region fills it exactly. Because both boxes come from that one scale, the enlarged picture keeps the source's aspect ratio — the check asserts that directly, which is what the earlier attempt got wrong when it stretched the picture to fit.

Paused, nothing is enlarged: the poster is the exact rendered frame, which is already cropped, so it fills the crop-shaped box as it is. Both states therefore frame the same region, and playing or pausing does not move the picture.

An absent crop, an unmeasured preview, a crop reaching outside its source and a source with no dimensions each fall back to the whole frame rather than producing a nonsense box.

In the window, the crop is applied only after the view has been measured: the check confirms the styles are the whole frame before a frame is laid out, and that the rendered box takes the crop's shape — within one percent of the crop's own ratio — and sits inside the preview area afterwards.

An exact render already contains cropped pixels, so the view leaves it alone; only the source copy is cropped by the view.

## The snapshot now shows what the window shows

The measurement reads a laid-out document, which the headless snapshot never produced: it built a document and rendered once. It now lays out, presents once and then renders, so a snapshot shows the same cropped box a person would see. A snapshot of a cropped project was rendered and looked at.

## Limitations

The enlarged picture is clipped to the crop box by `overflow:hidden`, and that clipping has not been observed: it only happens while frames are running, which needs native decoders and a prepared preview copy, so no headless check or snapshot can show it. If the engine does not clip a native video surface to its parent, a cropped clip will play showing more than the crop — inside the preview area, which also clips — and that will be visible the first time a person plays a cropped clip. The geometry either side of that is covered.

Only one crop, one source size and one preview size are exercised in the window; the rest is covered by the geometry check. Playing and pausing switches the picture between two boxes, which has not been watched for flicker.
