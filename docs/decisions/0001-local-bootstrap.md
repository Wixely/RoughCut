# 0001: Local repository and implementation handoff

- Date: 2026-09-18
- Status: Accepted for setup
- Source: User request to create the local RoughCut repository and prepare context for another agent
- Owner: Setup agent
- Review: Before implementation scope changes or remote creation

## Context and decision

PLAN contains a detailed RoughCut discovery note and dated feasibility research. The user has now authorized a local repository. Initialize a sibling RoughCut Git repository on `main`, with a complete implementation brief, agent instructions, work queue, acceptance plan and handoff.

The new repository owns future implementation context. PLAN retains discovery history, shared preferences and canonical external research. Keep a pointer in its inbox while hosting remains undecided, following the existing local-only project convention. The prior note's requirement to choose hosting before any repository creation is superseded for local initialization by this explicit request; a remote still needs a hosting decision.

## Consequences

Local contracts/export work can begin without selecting a remote, license, inference backend or desktop toolkit. No application implementation or dependency adoption is claimed by this setup. No runnable project means a VS Code debugger is not meaningful yet; the first runnable slice must supply and verify one.

The brief preserves confirmed requirements separately from proposed architecture and MVP scope. Research remains dated evidence, not proof of current package compatibility. Local links to PLAN assume sibling checkouts; update them if the workspace is moved.
