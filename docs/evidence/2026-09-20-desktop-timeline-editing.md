# Desktop timeline editing evidence

- Date: 2026-09-20
- Platform: Windows 10 Enterprise, .NET SDK 10.0.300, FFmpeg/FFprobe `N-124591-g5b38e6eafb-20260522`
- Scope: RC-06 revision-safe clip trim, split and reorder

The timeline card now edits the selected clip. IN/OUT fields with **Apply trim** set the retained source interval, **Split** divides the clip at the selected source frame, and **Earlier**/**Later** exchange it with a neighbour. Each control saves one revision-checked edit through the existing application boundary, and every one is reversible through the same session undo/redo stack that already carried speaker labels and crops.

Trim uses the new `set-range` edit action rather than `trim`, because `trim` may only shrink a clip and therefore cannot express the inverse of itself. Undoing a split removes the new clip and restores the original interval in one transactional batch, so no partial split is ever saved. See [decision 0017](../decisions/0017-reversible-timeline-editing.md).

Executed:

```powershell
dotnet build RoughCut.slnx
dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli src\RoughCut.Cli\bin\Debug\net10.0\roughcut.dll --desktop src\RoughCut.Desktop\bin\Debug\net10.0\roughcut-desktop.dll --mcp src\RoughCut.Mcp\bin\Debug\net10.0\roughcut-mcp.dll
dotnet run --project src\RoughCut.Desktop --no-build -- snapshot <three-clip-project.json> artifacts\debug-three.png
```

Observed: managed verification passed 57 checks with no failures, including two new ones. The core check proves `set-range` restores an interval a prior trim dropped without mutating the previous revision, and that it still rejects intervals past the source duration, empty intervals and unknown clips while `trim` continues to refuse any widening. The desktop check splits a four-second fixture at the selected speech frame, trims the resulting clip, moves it earlier, then undoes and redoes all three. Each step advanced the saved revision by exactly one — revision 10 after three edits, three undos and three redos — and the undone project was byte-identical in timeline shape to the original single clip.

Visual inspection of a 1280×800 snapshot with a three-clip fixture confirmed the controls fit the timeline card without overlap in the worst case, with all four buttons visible, and that moving the controls out of the review panel restored transcript visibility. A first attempt placed the clip editor in the right-hand panel, where CupriFace text fields rendered wider than their flex containers and collided with the buttons while squeezing the transcript to a single row; the controls now sit under the clips they act on.

Limitations: no physical pointer, keyboard or window interaction was exercised — the evidence is a headless render plus API-level checks. The Windows x64 NativeAOT CLI path was **not** re-run: this machine no longer has the MSVC platform linker (`Desktop development with C++`), so `dotnet publish -p:PublishAot=true` fails before compiling. The FFmpeg build first found on `PATH` is a 2018 `N-91454` build, too old for this project's `ffprobe` and `fps_mode` usage, and fails every media check; a separately installed current build was used instead. Clip removal is still CLI/MCP only. Trim remains numeric — there is no draggable timeline handle — and the crop editor's fourth numeric field is still clipped by the narrow review panel, which predates this slice.
