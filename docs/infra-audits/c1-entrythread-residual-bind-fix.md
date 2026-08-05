# C1 fix — residual enqueue requires bound EntryThreadId

**Date:** 2026-08-04  
**Evidence:** Claude C boot-walk (seq0193): IOPFILE enqueued residual but never drained  
**Root cause:** table full → `EntryThreadId=-1` → still checkpoint-enqueue → Drain silent drop  

## Fix (YIELD_START path only)

1. **Enqueue guard:** residual only if `EntryThreadId >= 1`; else TRACE skip and continue first-call budget.  
2. **Drain TRACE:** log DROP unbound (should be rare after #1).  
3. **Free entry slots only on pure budget exhaust** without residual — **not** on clean `returned` / residentSpin / bootQuanta (C1.2 re-arm + UniqueStacks smoke).

```text
no residual without resumable entry thread
  free only budget-exhaust dead ends (not clean return)
```

