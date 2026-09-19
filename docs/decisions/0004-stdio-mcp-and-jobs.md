# 0004: Use the official .NET MCP SDK for the bounded stdio host

- Date: 2026-09-19
- Status: Accepted for the local MVP host
- Review trigger: MCP transport expansion, SDK upgrade, NativeAOT requirement, or job-resume implementation

## Decision

Use `ModelContextProtocol` 1.4.0 for the local stdio server and protocol tests. Keep application operations and durable export jobs in a host-independent project. Require a configured workspace root and expose only workspace-relative paths. Return frames as MCP `ImageContentBlock` values and accept bounded base64 PNG data for portable image assets.

Persist export checkpoints as versioned JSON, allow one active export, cap retained records at 100 and expose explicit status/cancel tools. Interrupted work is marked failed on restart instead of being presented as resumed.

## Evidence and limits

The installed local MCPHub tools and sibling MCPSharp source were inspected before selecting a dependency. MCPSharp uses the official SDK and its stdio transport, while MCPHub pins version 1.4.0. No external MCP service was installed or configured.

The Windows executable tests prove stdio discovery/calls, an image-bearing response, incoming PNG import, root isolation, revision conflict reporting and durable cancellation. HTTP transport, remote authentication, Windows Service/systemd/Docker hosting, Linux execution, automatic resume and NativeAOT for the MCP host remain outside this decision.
