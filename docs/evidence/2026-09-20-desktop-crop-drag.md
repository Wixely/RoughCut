# Desktop direct crop evidence

- Date: 2026-09-20
- Platform: Windows, .NET SDK 10.0.300
- Scope: RC-06 direct crop movement and corner resizing

The source overlay now exposes a draggable crop rectangle with four corner handles. Pointer movement converts display coordinates back to integer source pixels. Moving preserves crop size, corner resizing preserves the opposite corner, and all modes clamp to the source frame with a minimum one-pixel width and height. Live X/Y/width/height values remain visible; pointer release submits one existing revision-checked crop operation, while cancellation restores the starting values.

Executed:

```powershell
.\scripts\verify.ps1 -PublishAot
dotnet run --project src\RoughCut.Desktop --no-build -- snapshot <test-project.json> artifacts\debugcrop\crop-handles.png
```

Observed: managed and Windows x64 NativeAOT-CLI verification each passed 56 checks with no failures. Geometry checks cover movement and every corner at source boundaries. The rendered integration check finds the south-east handle in CupriFace's laid-out tree, captures a pointer, drags it, releases it and verifies one saved revision, exact source-pixel dimensions, refreshed preview and undo availability. Visual inspection of the 1280×800 snapshot confirmed four visible handles, the crop border, numeric values and cropped main preview remain aligned without panel overflow.

Limitations: physical mouse/touch interaction has not been exercised in a live window. The handles resize independently without aspect-ratio locking or edge-only handles. General clip trim, split and reorder controls remain.
