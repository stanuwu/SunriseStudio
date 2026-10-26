# Releasing Solforge

Releases, and any build given to anyone else, are made from a clean `publish\`, produced by:

```powershell
.\publish.ps1
```

Never share, upload or attach anything from `obj\` or `bin\`.

## Build

`publish.ps1` builds the self-contained `Solforge.exe` and `solforge-mcp.exe` into a fresh `publish\`. It maps source
paths to `/_/` in the embedded PDBs and embeds the license notices.

## Release checklist

1. `.\verify.ps1` passes.
2. `.\publish.ps1` ends with "Published to ...\publish".
3. Upload only files from `publish\`.
