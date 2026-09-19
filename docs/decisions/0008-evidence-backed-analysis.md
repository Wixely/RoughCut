# 0008: Persist evidence before applying conservative editorial proposals

- Date: 2026-09-19
- Status: Accepted for the bounded MCP/CLI MVP
- Review trigger: semantic provider adoption, new evidence/action kinds, automatic-policy expansion, confidence calibration or analysis caching

## Decision

Keep semantic inference behind `IContentAnalyzer` and accept the same provider-neutral submission through CLI and MCP. Persist source-time evidence, observations, qualitative certainty, provider/model provenance, the user prompt and a proposal revision in the project JSON before any edit is applied.

Map observations to current timeline clips deterministically. The `review` policy never approves a removal automatically. The `auto-high-certainty` policy approves only a provider recommendation of `remove` with qualitative `high` certainty; all lower-certainty removals remain review items. Treat certainty as an uncalibrated label rather than a probability.

Apply automatic decisions only against their exact proposal revision. Allow explicit proposal IDs as the reviewed headless override, but only when the provider recommended removal. Merge overlapping selected intervals, preserve surrounding material and clear proposals after application or another timeline edit.

## Consequences

An MCP agent can inspect existing transcript/frame content, submit bounded evidence and complete a review or automatic workflow without a GUI. Invalid references, stale revisions and uncertain automatic removals fail safely. Provider output cannot inject edit operations, shell commands or media-tool arguments.

This decision adopts MCP-supplied analysis as the first provider mode. It does not choose a standalone semantic model, permit implicit cloud disclosure or establish real-media classification quality. Representative user-labelled acceptance, provider selection, long-video reconciliation and caching remain later work.

## Evidence

See the [analysis guide](../analysis.md) and [RC-04 evidence](../evidence/2026-09-19-rc04-analysis.md).
