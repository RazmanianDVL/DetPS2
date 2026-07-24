# EE / IOP JIT Rules (DetPS2)

**Status**: Spec for Phase 32.

## Det mode (law)

- JIT output must be **bit-identical** to the interpreter for all Det paths.  
- Nightly / CI: fixture suite runs interp and JIT; MasterCycles + FB hash must match.  
- Any mismatch blocks merge.

## Perf mode

- May use faster approximations only if **not** used for netplay or Det hashes.  
- Solo play default once Phase 29+32 land.

## Implementation order

1. Basic-block cache keyed by PC.  
2. Emit IL (`DynamicMethod`) or x64.  
3. Invalidate on self-modifying code heuristics (if observed).  
4. IOP second; VU micro optional worker with deterministic join.

## Testing

- `Jit_Parity_VsInterp` smoke pack  
- Random block fuzz vs interp  
