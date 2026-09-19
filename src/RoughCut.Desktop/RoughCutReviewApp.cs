using CupriFace;
using CupriFace.Binding;
using CupriFace.Interaction;
using RoughCut.Core;
using SkiaSharp;

namespace RoughCut.Desktop;

public sealed class RoughCutReviewApp(DesktopReviewSession session) : CupriApp
{
    private readonly ReviewModel _model = new();
    private CupriDocument? _document;

    public override string Title => "RoughCut Review";
    public override int Width => 1280;
    public override int Height => 800;
    public override bool DarkWindowChrome => true;
    public override SKColor Background => new(0x0b, 0x0f, 0x17);
    public override object Model => _model;
    public override string Html => Markup;
    public override string Css => Styles;

    public override void Configure(CupriDocument document)
    {
        _document = document;
        Rebuild();
        document.OnClick(".speech-row", e => Run(() => session.SelectSegmentAsync(Required(e, "data-id"))));
        document.OnClick(".evidence-row", e => Run(() => session.SelectEvidenceAsync(Required(e, "data-id"))));
        document.OnClick(".speaker-row", e =>
        {
            session.SelectSpeaker(Required(e, "data-id"));
            Rebuild();
            document.Refresh();
        });
        document.OnClick(".save-label", _ => Run(() => session.RenameSelectedSpeakerAsync(_model.SelectedLabel)));
        document.OnClick(".undo", _ => Run(() => session.UndoAsync()));
        document.OnClick(".redo", _ => Run(() => session.RedoAsync()));
        document.OnClick(".reload", _ => Run(() => session.ReloadAsync()));
    }

    private void Run(Func<Task> action)
    {
        try
        {
            _model.Status = "Working…";
            _document?.Refresh();
            action().GetAwaiter().GetResult();
            _model.Status = "Ready";
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or ProjectValidationException or
            RevisionConflictException or RoughCut.Media.MediaToolException)
        {
            _model.Status = exception.Message;
        }
        Rebuild(preserveStatus: true);
        _document?.Refresh();
    }

    private void Rebuild(bool preserveStatus = false)
    {
        var project = session.Project;
        _model.ProjectTitle = Path.GetFileName(session.ProjectPath);
        _model.Revision = project.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _model.PreviewDataUri = session.Preview is null ? "" :
            "data:image/png;base64," + Convert.ToBase64String(session.Preview.Png);
        _model.Selection = session.Selection;
        _model.Crop = session.Crop;
        _model.Speakers = project.Speakers.Select(item => new SpeakerRow
        {
            Id = item.Id,
            Label = item.Label,
            CssClass = item.Id == session.SelectedSpeakerId ? "selected" : ""
        }).ToArray();
        _model.SelectedLabel = project.Speakers.FirstOrDefault(item => item.Id == session.SelectedSpeakerId)?.Label ?? "";
        _model.Segments = project.Speech.Take(500).Select(item => new SpeechRow
        {
            Id = item.Id,
            Time = FormatTime(item.Start, project.TimeBase),
            Speaker = item.SpeakerIds.Length == 0 ? "Unknown" : string.Join(" + ", item.SpeakerIds.Select(id =>
                project.Speakers.FirstOrDefault(speaker => speaker.Id == id)?.Label ?? id)),
            Text = item.Text,
            Badge = item.Overlap ? "overlap" : item.Assignment,
            CssClass = item.Id == session.SelectedSegmentId ? "selected" : ""
        }).ToArray();
        _model.Clips = ProjectValidator.MapTimeline(project).Take(200).Select(item => new ClipRow
        {
            Id = item.ClipId,
            Range = $"{FormatTime(item.OutputIn, project.TimeBase)}–{FormatTime(item.OutputOut, project.TimeBase)}",
            Source = item.AssetId
        }).ToArray();
        _model.Evidence = project.Proposals.Take(100).Select(proposal =>
        {
            var observation = project.Observations.First(item => item.Id == proposal.ObservationId);
            return new EvidenceRow { Id = observation.Id, Decision = proposal.Decision, Summary = observation.Summary };
        }).ToArray();
        _model.Truncation = project.Speech.Length > 500 ? $"Showing 500 of {project.Speech.Length} transcript rows" : "";
        if (!preserveStatus) _model.Status = "Ready";
    }

    private static string Required(CupriPointerEvent e, string attribute) =>
        e.Element.GetAttribute(attribute) ?? throw new InvalidDataException($"UI element is missing {attribute}.");

    private static string FormatTime(long ticks, TimeBase timeBase)
    {
        var seconds = (decimal)ticks * timeBase.Numerator / timeBase.Denominator;
        var span = TimeSpan.FromSeconds((double)seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss\.fff") : span.ToString(@"m\:ss\.fff");
    }

    private const string Markup = """
        <body>
          <main class="shell">
            <header class="topbar">
              <div><div class="brand">ROUGH<span>CUT</span></div><div class="project">{{ProjectTitle}} · revision {{Revision}}</div></div>
              <div class="actions"><span class="status"><span class="status-dot"></span>{{Status}}</span><cupri-button class="reload" variant="ghost">Reload</cupri-button><cupri-button class="undo">Undo</cupri-button><cupri-button class="redo">Redo</cupri-button></div>
            </header>
            <section class="workspace">
              <div class="stage-column">
                <div class="preview-card">
                  <div class="preview"><cupri-image src="{{PreviewDataUri}}" fit="contain" alt="Selected timeline frame"></cupri-image></div>
                  <div class="preview-meta"><strong>{{Selection}}</strong><span>{{Crop}}</span></div>
                </div>
                <div class="timeline-card">
                  <div class="section-title">Timeline</div>
                  <div class="timeline"><div class="clip" data-repeat="Clips"><strong>{{Id}}</strong><span>{{Range}}</span><small>{{Source}}</small></div></div>
                </div>
                <div class="evidence-card">
                  <div class="section-title">Editorial evidence</div>
                  <div class="empty">Select an item to seek to its retained source frame.</div>
                  <button class="evidence-row" data-repeat="Evidence" data-id="{{Id}}"><span class="decision">{{Decision}}</span><span>{{Summary}}</span></button>
                </div>
              </div>
              <aside class="review-panel">
                <div class="section-title">Speakers</div>
                <button class="speaker-row {{CssClass}}" data-repeat="Speakers" data-id="{{Id}}"><span class="avatar">●</span><span>{{Label}}</span><small>{{Id}}</small></button>
                <div class="rename"><cupri-textfield value="{{SelectedLabel}}" placeholder="Speaker label"></cupri-textfield><cupri-button class="save-label">Save label</cupri-button></div>
                <div class="section-title transcript-title">Transcript</div>
                <div class="truncation">{{Truncation}}</div>
                <div class="transcript"><button class="speech-row {{CssClass}}" data-repeat="Segments" data-id="{{Id}}"><div class="speech-head"><span>{{Time}}</span><strong>{{Speaker}}</strong><small>{{Badge}}</small></div><p>{{Text}}</p></button></div>
              </aside>
            </section>
          </main>
        </body>
        """;

    private const string Styles = """
        :root { --bg:#0b0f17; --panel:#121824; --panel2:#171f2e; --line:#283348; --text:#eef3fb; --muted:#91a0b7; --accent:#ff9f43; }
        body { margin:0; background:var(--bg); color:var(--text); font-family:Arial,sans-serif; }
        .shell { height:100vh; display:flex; flex-direction:column; }
        .topbar { height:74px; padding:0 24px; display:flex; align-items:center; justify-content:space-between; border-bottom:1px solid var(--line); background:#0e141f; }
        .brand { font-size:22px; font-weight:bold; letter-spacing:2px; } .brand span { color:var(--accent); }
        .project { color:var(--muted); font-size:12px; margin-top:5px; } .actions { display:flex; gap:8px; align-items:center; }
        .status { display:flex; align-items:center; gap:7px; color:var(--muted); font-size:11px; margin-right:8px; }
        .workspace { flex:1; min-height:0; display:grid; grid-template-columns:minmax(0,1fr) 380px; gap:14px; padding:14px; }
        .stage-column { min-width:0; display:grid; grid-template-rows:minmax(350px,1fr) 118px 130px; gap:14px; }
        .preview-card,.timeline-card,.evidence-card,.review-panel { background:var(--panel); border:1px solid var(--line); border-radius:12px; }
        .preview-card { min-height:350px; padding:12px; display:flex; flex-direction:column; }
        .preview { flex:1; min-height:300px; display:flex; align-items:center; justify-content:center; background:#05070b; border-radius:8px; overflow:hidden; }
        .preview cupri-image { width:100%; height:100%; }
        .preview-meta { padding:10px 4px 0; display:flex; justify-content:space-between; gap:12px; color:var(--muted); font-size:12px; } .preview-meta strong { color:var(--text); }
        .section-title { padding:12px 14px 8px; color:var(--muted); text-transform:uppercase; letter-spacing:1.2px; font-size:11px; font-weight:bold; }
        .timeline { display:flex; gap:6px; padding:0 12px 12px; overflow:hidden; }
        .clip { min-width:112px; flex:1; padding:9px; border-radius:7px; background:var(--panel2); border-top:3px solid var(--accent); display:flex; flex-direction:column; gap:3px; }
        .clip span,.clip small { color:var(--muted); font-size:10px; }
        .evidence-card { max-height:150px; overflow:hidden; padding-bottom:8px; } .empty { color:var(--muted); font-size:11px; padding:0 14px 8px; }
        button { color:inherit; font:inherit; } .evidence-row,.speaker-row,.speech-row { width:100%; border:0; text-align:left; cursor:pointer; }
        .evidence-row { display:flex; gap:8px; padding:7px 14px; background:transparent; } .decision { color:var(--accent); font-weight:bold; text-transform:uppercase; font-size:10px; }
        .review-panel { min-height:0; display:flex; flex-direction:column; overflow:hidden; }
        .speaker-row { display:grid; grid-template-columns:18px 1fr auto; gap:8px; align-items:center; padding:8px 14px; background:transparent; border-left:3px solid transparent; }
        .speaker-row:hover,.speaker-row.selected,.speech-row:hover,.speech-row.selected { background:#202a3c; } .speaker-row.selected { border-left-color:var(--accent); }
        .speaker-row small { color:var(--muted); font-size:9px; } .avatar { color:var(--accent); }
        .rename { display:grid; grid-template-columns:1fr auto; gap:8px; padding:10px 14px 14px; border-bottom:1px solid var(--line); }
        .transcript-title { padding-top:14px; } .truncation { color:var(--accent); font-size:10px; padding:0 14px 6px; }
        .transcript { flex:1; min-height:0; overflow-y:auto; padding:0 7px 10px; }
        .speech-row { padding:10px; margin-bottom:5px; border-radius:7px; background:transparent; }
        .speech-head { display:flex; gap:8px; align-items:center; font-size:10px; color:var(--muted); } .speech-head strong { color:var(--accent); } .speech-head small { margin-left:auto; }
        .speech-row p { margin:5px 0 0; line-height:1.35; font-size:13px; color:var(--text); }
        .status-dot { width:7px; height:7px; border-radius:50%; background:#49d17d; }
        """;
}

[CupriBindable]
public sealed partial class ReviewModel
{
    public string ProjectTitle { get; set; } = "";
    public string Revision { get; set; } = "";
    public string PreviewDataUri { get; set; } = "";
    public string Selection { get; set; } = "";
    public string Crop { get; set; } = "";
    public string SelectedLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public string Truncation { get; set; } = "";
    public SpeakerRow[] Speakers { get; set; } = [];
    public SpeechRow[] Segments { get; set; } = [];
    public ClipRow[] Clips { get; set; } = [];
    public EvidenceRow[] Evidence { get; set; } = [];
}

[CupriBindable] public sealed partial class SpeakerRow { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class SpeechRow { public string Id { get; set; } = ""; public string Time { get; set; } = ""; public string Speaker { get; set; } = ""; public string Text { get; set; } = ""; public string Badge { get; set; } = ""; public string CssClass { get; set; } = ""; }
[CupriBindable] public sealed partial class ClipRow { public string Id { get; set; } = ""; public string Range { get; set; } = ""; public string Source { get; set; } = ""; }
[CupriBindable] public sealed partial class EvidenceRow { public string Id { get; set; } = ""; public string Decision { get; set; } = ""; public string Summary { get; set; } = ""; }
