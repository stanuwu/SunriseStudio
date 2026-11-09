# Releasing Solforge

Releases, and any build given to anyone else, are made from a clean `publish\`, produced by:

```powershell
.\publish.ps1 -Output publish-staging
```

Never share, upload or attach anything from `obj\` or `bin\`.

## Build

`publish.ps1` builds the self-contained `Solforge.exe` and `solforge-mcp.exe` into a new or empty output folder (default `publish\`), along with the project skills under `skills\`. The separate Solforge AI desktop app and its runtime are not built or packaged. `build-solforge.ps1` creates a clean MCP-only test publish and checks the editor, MCP server and skill playbooks. The publish maps source paths to `/_/` in embedded PDBs and embeds the license notices.

## Release checklist

1. `.\verify.ps1` passes.
2. `.\publish.ps1 -Output <new folder>` ends with "Published to ...".
3. Upload only files from that clean publish output.
