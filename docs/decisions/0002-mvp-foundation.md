# 0002: Start with a shared local-media foundation

- Date: 2026-09-19
- Status: Implementation sequencing decision; product/runtime tradeoffs remain provisional
- Source: User request to identify the MVP and begin implementation
- Owner: Implementation agent
- Review: After the export feasibility slice and before adopting model/UI dependencies

## Decision

Use the [working MVP](../mvp.md) to organize the existing requirements. Begin with .NET 10 core contracts, portable JSON persistence, a bounded FFmpeg adapter and CLI. Keep speech/image contracts in the baseline so later MCP/UI work does not create separate project formats. Use source-generated JSON with reflection disabled, and prove the CLI under Windows NativeAOT.

Implement source-frame extraction alongside the contracts because it directly exercises rational timing and the recently requested agent vision workflow. RC-02 export safety remains the next implementation gate. Do not claim MCP support merely because frame bytes are available in the media library.

## Consequences and evidence

This slice uses only the .NET shared framework and the already accepted FFmpeg/FFprobe executable dependency. No reusable sibling package is adopted or modified yet. An executable C# test harness avoids selecting a test-framework package during bootstrap and records meaningful persistence/media checks. Add a standard test runner later if it materially improves the workflow.

The [execution evidence](../evidence/2026-09-19-foundation.md) covers Windows managed and NativeAOT CLI behavior. Runtime model choices, desktop toolkit, Linux acceptance, export codec matrix and MCP transport/client compatibility remain open. The earlier bootstrap decision remains historical; its statement that nothing is runnable is superseded by this implementation and the current handoff.
