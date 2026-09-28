using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

/// One thing this project can be exported as. `Copy` separates the two kinds, because they make different
/// promises: a copy keeps the source's own packets and is fast, but its cuts land on the source's keyframes,
/// where a re-encode lands the cut exactly and rewrites every frame. `AudioCodec` reads "source" for a copy,
/// since a copy carries whatever the source holds.
public sealed record ExportFormatOption(string Name, string Label, string Extension,
    string VideoCodec, string AudioCodec, bool Copy);

/// What a caller may offer for one project, in the order it should be offered: copies first, because keeping
/// the source's own packets is the cheapest answer and the right default for a cut-down.
public sealed record ExportFormatList(int SchemaVersion, long Revision, ExportFormatOption[] Options);
