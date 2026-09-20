# 0016: Public GitHub repository

- Date: 2026-09-20
- Status: Accepted
- Source: Wixely requested GitHub repository creation and push, then explicitly selected public visibility
- Owner: Implementation agent
- Review: Before changing visibility, licensing or distribution scope

## Decision

Publish RoughCut at https://github.com/Wixely/RoughCut on `main`. Apply the MIT License to project-owned code, documentation and branding, following Wixely's established open-source preference. Third-party packages retain their own licenses and notices; the vendored CupriFace packages include MIT license files and recorded release provenance.

This supersedes the local-only hosting deferral in decision 0001. Preserve that record as setup history. GitHub publication does not imply that the MVP, Linux validation or release packaging is complete.

## Evidence and consequences

Before publication, inspect the complete outgoing history, commit identities, text and binary metadata. Publish only `main`; local media, models and generated execution artifacts remain excluded. Preserve generated-image provenance and upstream package contents. Package binaries contain upstream CI build paths; these are not local user paths.

Owner: Implementation agent. Next: verify the remote branch matches the reviewed local commit, then continue RC-06 timeline editing and the pending live Windows icon check.
