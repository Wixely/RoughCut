# Evidence-backed analysis and editorial proposals

- Updated: 2026-09-19
- Owner: Implementation agent
- Review: Before adding a local/remote inference provider, changing automatic policy or accepting new evidence kinds
- Status: Provider-neutral MCP/CLI workflow verified on Windows with labelled deterministic fixtures

RoughCut keeps source analysis separate from editorial decisions. An inference provider or MCP caller submits bounded source-time evidence and observations. RoughCut validates the submission, maps each observation onto current timeline clips and persists revision-bound proposals. Model output never supplies commands or FFmpeg arguments.

## Submission contract

An `AnalysisSubmission` identifies one timed source asset, provider and model. Evidence intervals use project ticks and may reference transcript segments and representative frame timestamps. Supported evidence kinds are `transcript`, `frame`, `chapter`, `activity` and `metadata`. Observations reference overlapping evidence and contain a label, bounded explanation, qualitative certainty (`low`, `medium` or `high`) and recommendation (`retain`, `remove` or `review`).

Every source, interval, speech reference, frame timestamp and evidence relationship is validated. The current bounds are 5,000 evidence items, 2,000 observations, 5,000 proposals, 100 evidence references per observation and 100 speech/frame references per evidence item. The media fingerprint is checked before the project revision is saved.

The example [analysis submission](../examples/analysis-submission.json) is a synthetic labelled fixture, not a general advertisement detector.

## Decision policies

| Policy | Provider recommendation | Persisted decision |
| --- | --- | --- |
| `review` | `remove`, at any certainty | `review`; content stays in the timeline |
| `auto-high-certainty` | `remove` with `high` certainty | `remove` |
| Either policy | `remove` with `low` or `medium` certainty | `review`; content stays in the timeline |
| Either policy | `retain` | `retain` |
| Either policy | `review` | `review` |

Certainty labels are provider judgements, not calibrated probabilities. The automatic policy is explicit in every call and recorded with provider/model, prompt, source hash and proposal revision. The default examples and VS Code launch use `review`.

Applying proposals requires the exact revision that created them. Omitting proposal IDs applies only decisions marked `remove`; this supplies a fully headless automatic path. Supplying IDs is an explicit reviewed choice and is allowed only for proposals whose provider recommendation was `remove`. Overlapping ranges are merged, exact half-open intervals are removed and retained clip parts keep deterministic IDs. Any ordinary timeline edit clears existing proposals so stale decisions cannot be applied silently.

## CLI

```powershell
dotnet run --project src/RoughCut.Cli -- analyse `
  artifacts/demo/project.json examples/analysis-submission.json 1 review 'Remove advertisements'

# Applies only auto-approved decisions. With review policy this fails if nothing was approved.
dotnet run --project src/RoughCut.Cli -- analysis-apply artifacts/demo/project.json 2

# Explicit reviewed selection:
dotnet run --project src/RoughCut.Cli -- analysis-apply artifacts/demo/project.json 2 proposal-1
```

The equivalent MCP tools are `roughcut_save_analysis` and `roughcut_apply_analysis`. An MCP agent can inspect transcript/frame evidence with existing tools, submit observations, review the persisted proposal result and apply either automatic decisions or explicit proposal IDs without a GUI prompt.

## Limits

The verified slice proves contracts, validation, policy and exact edit application with labelled synthetic ad/no-ad/uncertain observations. It does not claim a trained detector, provider accuracy, calibrated certainty, shot/activity extraction, long-video chunk reconciliation, caching, or safe cloud disclosure. A local or explicitly enabled remote semantic provider can implement `IContentAnalyzer`; no standalone provider is selected yet. Representative user media and quality targets remain acceptance work.
