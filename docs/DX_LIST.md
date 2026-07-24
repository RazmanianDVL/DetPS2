# Deferred Titles (DX)

Titles that fail the majority campaign stay here with a **blocker tag** until fixed in Phase 39+.

Use `DxTracker` in tools/tests to promote tiers:

```csharp
var dx = new DxTracker();
dx.LoadMarkdown("docs/DX_LIST.md");
dx.Promote("some-id", "P2", "fixed GS CLUT");
dx.SaveMarkdown("docs/DX_LIST.md");
```

| id | Title | Tags | Notes |
|----|-------|------|-------|
| *(empty at v2.0 synthetic ship)* | | | |

## Tags

`EE_OP` · `GS_FMT` · `VU_MICRO` · `IOP_IRX` · `CDVD` · `SPU` · `IPU` · `OTHER`

DX titles are **excluded** from the majority denominator until promoted.
