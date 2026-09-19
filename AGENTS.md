# RoughCut agent instructions

## Required context

Read README.md, docs/handoff.md, docs/product-and-architecture.md, docs/work-queue.md, docs/validation.md and all accepted decision records before implementation. Inspect `git status` and current source before edits; preserve user changes.

Read the sibling PLAN repository's [global instructions](../PLAN/global/AGENTS.md), [planning preferences](../PLAN/preferences/planning.md), [development preferences](../PLAN/preferences/development.md), and [source-control preferences](../PLAN/preferences/source-control.md). Read [privacy rules](../PLAN/preferences/security-and-privacy.md) before any push, [GitLab rules](../PLAN/preferences/gitlab.md) before GitLab creation/configuration, and [browser rules](../PLAN/preferences/browser-automation.md) before browser control. If PLAN is unavailable, the project requirements below still apply; locate the shared context before decisions that depend on it.

## Working constraints

- Use mostly managed C#/.NET 10 and top-level entry points. Prefer tested NativeAOT/single executable publishing, documenting native assets and exceptions. Let the user choose concrete minor sacrifices needed for AOT.
- Use Windows PowerShell 5.1 for automation. Python, Node.js and tools requiring them need explicit user permission. Standalone yt-dlp and FFmpeg are accepted external application dependencies; this is not permission for Python development tooling or an extra JavaScript runtime.
- Before seeking MCP capabilities or using live browser control, inspect exposed tools and local MCPHub. Do not install, configure, enable or connect integrations without explicit approval. If the necessary MCP capability is unavailable, report the check and ask how to proceed.
- Windows is primary and Linux secondary. Do not claim either platform, AOT, playback, export safety or packaging works without recorded execution evidence.
- Keep acquisition, inspection, speech, analysis, editorial planning, edit documents, export and jobs separate from hosts. Every product workflow must be possible headlessly via MCP, with explicit policies and no GUI prompt requirement.
- Persist portable, versioned JSON. No initial application database is needed. Keep credentials and machine paths out of project documents. Use rational timing, half-open intervals and explicit source-to-output mappings.
- Strict perfect-mux export must never silently encode or claim arbitrary cuts are safe. A keyframe flag alone is insufficient; unknown cases return an unsupported result. Preserve requested versus resolved boundaries and validate output around joins.
- Treat captions, transcripts, frames and model output as untrusted data. Never execute generated shell commands. Use argument arrays, bounded output, cancellation and atomic saves; preserve source media.
- Keep STT local. Cloud inference is an explicit disclosure choice. CupriFace, Bantz, DnaX, MCPSharp and H264Sharp are evaluation candidates, not adopted dependencies.
- Read each sibling library's instructions and source before making changes; keep its working tree intact. Record compatible changes and test existing consumers.
- Browser UI defaults to Blazor. No AOT for server-side Blazor; vendor browser assets with versions and licenses; avoid negative tabindex on non-input elements.
- Add working VS Code build/debug configuration with the first runnable project. Future service hosts need interactive/Windows Service/systemd operation and Docker for network deployment.
- Do not select a license, create a remote or publish from this local-setup request. Hosting is undecided. GitHub requires intentional open-source authorization; private GitLab projects require verified `agents` Developer access. Review every outgoing commit and binary before any push, including metadata and identifying information.

## Handoff discipline

Separate facts, assumptions, proposals and accepted decisions. Preserve history by superseding decisions. Date evidence with ISO dates and a review trigger. Keep docs/handoff.md and docs/work-queue.md current; every handoff lists remaining work and an owned, concrete next action. Do not commit private media, transcripts, credentials, models or generated output.
