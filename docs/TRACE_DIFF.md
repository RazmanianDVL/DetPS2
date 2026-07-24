# Trace diff guide

## Format

Each instruction line:

```
C={masterCycles} PC=0x{pc:X8} OP=0x{opcode:X8}
```

Notes:

```
C={masterCycles} NOTE {message}
```

## Diffing two runs

1. Enable tracer on both runs (or export `Tracer.ExportText()`).
2. Use `Tracer.Diff(entriesA, entriesB)` for a simple set-style +/- report.
3. For ordered diff, pipe exported text through standard `diff -u`.

## Determinism checks

- Same input tape + same ELF ⇒ identical trace prefixes for equal cycle counts
- Diverging PC at cycle N usually means missing MMIO, HLE, or float policy break
