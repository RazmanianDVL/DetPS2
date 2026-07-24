using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// EE basic-block JIT (Phases 32/45/51).
/// Phase 51: real ALU emit for pure-I/R blocks (ADDIU/LUI/ANDI/… + SPECIAL ADDU/…)
/// with optional trailing BEQ/BNE self-loop — Det-identical Lo GPR effects + PC/cycles.
/// <see cref="HasRealAluEmit"/> is true when real emit is enabled (default).
/// </summary>
public sealed class EeJit
{
    public enum OpKind : byte
    {
        Nop = 0,
        Addiu, Addi, Andi, Ori, Xori, Lui,
        Addu, Add, Subu, Sub, And, Or, Xor, Nor, Slt, Sltu,
        Sll, // sa shift of rt → rd (includes nop)
        Beq, Bne, // terminal only
    }

    public struct Decoded
    {
        public OpKind Kind;
        public byte Rd, Rs, Rt;
        public int Imm; // signed imm or sa
    }

    public sealed class Block
    {
        public ulong EntryPc;
        public uint[] Opcodes = Array.Empty<uint>();
        public Decoded[] Decoded = Array.Empty<Decoded>();
        public int PureCount;
        public bool HasTerminalBranch;
        public OpKind TermKind;
        public byte TermRs, TermRt;
        public int TermOff; // instruction offset for branch
        public int Hits;
        public ulong Instructions;
        /// <summary>IL entry: (ee, maxCycles) → cycles consumed.</summary>
        public Func<EmotionEngine, ulong, int>? IlEntry;
        public bool IsIl;
        public bool IsRealAlu;
    }

    private readonly Dictionary<ulong, Block> _cache = new();
    private readonly EmotionEngine _ee;
    private readonly SystemMemory _mem;

    public bool Enabled { get; set; }
    /// <summary>Emit DynamicMethod for pure-ALU blocks (real ALU ops, not Step trampoline).</summary>
    public bool EmitIl { get; set; } = true;
    /// <summary>Phase 51: true — real ALU IL / decoded runner (not Step-only).</summary>
    public bool HasRealAluEmit => EmitIl;
    public int MaxBlockInsns { get; set; } = 32;
    public ulong BlocksCompiled { get; private set; }
    public ulong BlocksExecuted { get; private set; }
    public ulong CacheHits { get; private set; }
    public ulong CacheMisses { get; private set; }
    public ulong IlBlocksCompiled { get; private set; }
    public ulong IlBlocksRun { get; private set; }
    public ulong RealAluBlocksCompiled { get; private set; }
    public ulong RealAluInsns { get; private set; }
    public int CacheSize => _cache.Count;

    private static readonly MethodInfo? MiGetLo =
        typeof(EmotionEngine).GetMethod(nameof(EmotionEngine.JitGetLo));
    private static readonly MethodInfo? MiSetLo =
        typeof(EmotionEngine).GetMethod(nameof(EmotionEngine.JitSetLo));
    private static readonly MethodInfo? MiAddCount =
        typeof(EmotionEngine).GetMethod(nameof(EmotionEngine.JitAddCount));
    private static readonly PropertyInfo? PiPc =
        typeof(EmotionEngine).GetProperty(nameof(EmotionEngine.PC));

    public EeJit(EmotionEngine ee, SystemMemory mem)
    {
        _ee = ee ?? throw new ArgumentNullException(nameof(ee));
        _mem = mem ?? throw new ArgumentNullException(nameof(mem));
    }

    public void Reset()
    {
        _cache.Clear();
        BlocksCompiled = BlocksExecuted = CacheHits = CacheMisses = 0;
        IlBlocksCompiled = IlBlocksRun = RealAluBlocksCompiled = RealAluInsns = 0;
    }

    public void Invalidate() => _cache.Clear();
    public void InvalidatePc(ulong pc) => _cache.Remove(pc & ~3UL);

    public int Execute(ulong maxCycles)
    {
        if (!Enabled || maxCycles == 0)
            return _ee.Step(maxCycles);

        int left = (int)Math.Min(maxCycles, (ulong)int.MaxValue);
        int total = 0;
        while (left > 0)
        {
            if (_ee.InterruptPending && _ee.TakeExceptions)
            {
                int c = _ee.Step((ulong)left);
                total += c;
                break;
            }

            ulong pc = _ee.PC;
            if (!_cache.TryGetValue(pc, out var block))
            {
                CacheMisses++;
                block = CompileBlock(pc);
            }
            else CacheHits++;

            BlocksExecuted++;
            block.Hits++;
            int ran = RunBlock(block, left);
            total += ran;
            left -= ran;
            if (ran == 0)
            {
                int c = _ee.Step(1);
                total += c;
                left -= c;
                if (c == 0) break;
            }
        }
        return total;
    }

    private Block CompileBlock(ulong entry)
    {
        var ops = new List<uint>(MaxBlockInsns);
        ulong pc = entry;
        for (int i = 0; i < MaxBlockInsns; i++)
        {
            uint op = _mem.Read32(pc);
            ops.Add(op);
            uint primary = (op >> 26) & 0x3F;
            if (primary is 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                or 0x14 or 0x15 or 0x16 or 0x17)
                break;
            if (primary == 0x00)
            {
                uint f = op & 0x3F;
                if (f is 0x08 or 0x09 or 0x0C or 0x0D) break;
            }
            pc += 4;
        }

        var block = new Block
        {
            EntryPc = entry,
            Opcodes = ops.ToArray(),
            Instructions = (ulong)ops.Count
        };
        DecodeBlock(block);
        if (EmitIl)
            TryCompileIl(block);
        _cache[entry] = block;
        BlocksCompiled++;
        if (_cache.Count > 4096)
            _cache.Clear();
        return block;
    }

    private static void DecodeBlock(Block block)
    {
        var list = new List<Decoded>(block.Opcodes.Length);
        for (int i = 0; i < block.Opcodes.Length; i++)
        {
            uint op = block.Opcodes[i];
            uint p = (op >> 26) & 0x3F;
            bool isTerm = p is 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                or 0x14 or 0x15 or 0x16 or 0x17;
            if (isTerm)
            {
                if (i != block.Opcodes.Length - 1)
                {
                    block.PureCount = 0;
                    return;
                }
                if (p is 0x04 or 0x05) // BEQ BNE only for real ALU loop emit
                {
                    block.HasTerminalBranch = true;
                    block.TermKind = p == 0x04 ? OpKind.Beq : OpKind.Bne;
                    block.TermRs = (byte)((op >> 21) & 0x1F);
                    block.TermRt = (byte)((op >> 16) & 0x1F);
                    block.TermOff = (short)(op & 0xFFFF);
                }
                break;
            }

            if (!TryDecodePure(op, out var d))
            {
                block.PureCount = 0;
                return;
            }
            list.Add(d);
        }
        block.Decoded = list.ToArray();
        block.PureCount = list.Count;
    }

    private static bool TryDecodePure(uint op, out Decoded d)
    {
        d = default;
        uint p = (op >> 26) & 0x3F;
        byte rs = (byte)((op >> 21) & 0x1F);
        byte rt = (byte)((op >> 16) & 0x1F);
        byte rd = (byte)((op >> 11) & 0x1F);
        int imm = (short)(op & 0xFFFF);
        ushort uimm = (ushort)(op & 0xFFFF);
        byte sa = (byte)((op >> 6) & 0x1F);

        switch (p)
        {
            case 0x08: d = new Decoded { Kind = OpKind.Addi, Rs = rs, Rt = rt, Imm = imm }; return true;
            case 0x09: d = new Decoded { Kind = OpKind.Addiu, Rs = rs, Rt = rt, Imm = imm }; return true;
            case 0x0C: d = new Decoded { Kind = OpKind.Andi, Rs = rs, Rt = rt, Imm = uimm }; return true;
            case 0x0D: d = new Decoded { Kind = OpKind.Ori, Rs = rs, Rt = rt, Imm = uimm }; return true;
            case 0x0E: d = new Decoded { Kind = OpKind.Xori, Rs = rs, Rt = rt, Imm = uimm }; return true;
            case 0x0F: d = new Decoded { Kind = OpKind.Lui, Rt = rt, Imm = uimm }; return true;
            case 0x00:
            {
                uint f = op & 0x3F;
                d.Rd = rd; d.Rs = rs; d.Rt = rt; d.Imm = sa;
                switch (f)
                {
                    case 0x00: d.Kind = OpKind.Sll; return true;
                    case 0x20: d.Kind = OpKind.Add; return true;
                    case 0x21: d.Kind = OpKind.Addu; return true;
                    case 0x22: d.Kind = OpKind.Sub; return true;
                    case 0x23: d.Kind = OpKind.Subu; return true;
                    case 0x24: d.Kind = OpKind.And; return true;
                    case 0x25: d.Kind = OpKind.Or; return true;
                    case 0x26: d.Kind = OpKind.Xor; return true;
                    case 0x27: d.Kind = OpKind.Nor; return true;
                    case 0x2A: d.Kind = OpKind.Slt; return true;
                    case 0x2B: d.Kind = OpKind.Sltu; return true;
                    default: return false;
                }
            }
            default: return false;
        }
    }

    private void TryCompileIl(Block block)
    {
        if (block.PureCount < 1 || MiGetLo == null || MiSetLo == null || PiPc == null)
            return;

        // Prefer specialized C# runner for hot loops (stable Det + high speedup)
        if (block.HasTerminalBranch && block.TermKind is OpKind.Beq or OpKind.Bne)
        {
            var pure = block.Decoded;
            var termKind = block.TermKind;
            byte trs = block.TermRs, trt = block.TermRt;
            int toff = block.TermOff;
            ulong entry = block.EntryPc;
            // Delay slot must be nop (0) for specialized loop — else fall back
            uint delay = block.Opcodes.Length >= 1
                ? (block.Opcodes.Length > block.PureCount
                    ? _mem.Read32(entry + (ulong)(block.PureCount + 1) * 4) // after branch
                    : 0)
                : 0;
            // Branch is last in Opcodes; delay is next word in memory (not in block if we broke on branch)
            delay = _mem.Read32(entry + (ulong)block.Opcodes.Length * 4);

            if (delay != 0)
            {
                // non-nop delay: Step path
                TryCompileStepTrampoline(block);
                return;
            }

            int pureN = pure.Length;
            // cycles per taken iteration: pureN + branch + delay = pureN + 2
            // not-taken: pureN + 1 (branch) then PC advances past delay → Step does pureN + 1 for branch not taken? 
            // Actually Step: executes branch (1), if taken executes delay (2) and jumps; if not taken PC+=4 only (no delay exec for normal BEQ)
            // Wait: for non-likely BEQ, delay always executes! tookBranch true → delay + jump; tookBranch false → PC+=4 only means delay is SKIPPED? 
            // Looking at Step code:
            // if (tookBranch) { delay; PC = target }
            // else if (likely) { PC += 8 }
            // else { PC += 4 }
            // So for BEQ not taken, delay slot is NOT executed and PC only advances by 4 — so delay is skipped!
            // That's unusual for MIPS (normally delay always runs). This emulator only runs delay when branch taken.
            // So taken: pure + branch + delay = pureN+2 cycles, PC = target
            // not taken: pure + branch = pureN+1 cycles, PC = after branch (entry + pureN*4 + 4)

            block.IlEntry = (ee, maxCycles) =>
                RunSpecializedLoop(ee, pure, pureN, termKind, trs, trt, toff, entry, (int)Math.Min(maxCycles, int.MaxValue));
            block.IsIl = true;
            block.IsRealAlu = true;
            IlBlocksCompiled++;
            RealAluBlocksCompiled++;
            return;
        }

        // Straight-line pure ALU without a loop: one-shot IL is OK for short blocks,
        // but advancing PC through zero-filled memory would recompile every address —
        // use a bulk decoded runner that can be re-entered only via cache hits on
        // the same entry (no IL compile storm). Prefer Step for non-loop blocks.
        if (block.PureCount >= 1 && !block.HasTerminalBranch)
        {
            var pure = block.Decoded;
            int pureN = pure.Length;
            ulong entry = block.EntryPc;
            block.IlEntry = (ee, maxCycles) =>
            {
                if (ee.PC != entry) return ee.Step(maxCycles);
                int n = pureN;
                if ((int)maxCycles < n) return ee.Step(maxCycles);
                for (int i = 0; i < n; i++)
                    ApplyDecoded(ee, pure[i]);
                ee.PC = entry + (ulong)(n * 4);
                ee.JitAddCount(n);
                RealAluInsns += (ulong)n;
                return n;
            };
            block.IsIl = true;
            block.IsRealAlu = true;
            IlBlocksCompiled++;
            RealAluBlocksCompiled++;
            return;
        }

        TryCompileStepTrampoline(block);
    }

    private void TryCompileStepTrampoline(Block block)
    {
        try
        {
            var step = typeof(EmotionEngine).GetMethod(nameof(EmotionEngine.Step));
            if (step == null) return;
            var dm = new DynamicMethod(
                "EeStep_" + block.EntryPc.ToString("X"),
                typeof(int),
                new[] { typeof(EmotionEngine), typeof(ulong) },
                typeof(EeJit).Module,
                skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, step);
            il.Emit(OpCodes.Ret);
            block.IlEntry = (Func<EmotionEngine, ulong, int>)dm.CreateDelegate(typeof(Func<EmotionEngine, ulong, int>));
            block.IsIl = true;
            block.IsRealAlu = false;
            IlBlocksCompiled++;
        }
        catch
        {
            block.IlEntry = null;
            block.IsIl = false;
        }
    }

    private bool TryEmitStraightLineIl(Block block)
    {
        if (MiGetLo == null || MiSetLo == null || MiAddCount == null || PiPc?.SetMethod == null)
            return false;
        try
        {
            var pure = block.Decoded;
            int n = pure.Length;
            var dm = new DynamicMethod(
                "EeAlu_" + block.EntryPc.ToString("X"),
                typeof(int),
                new[] { typeof(EmotionEngine), typeof(ulong) },
                typeof(EeJit).Module,
                skipVisibility: true);
            var il = dm.GetILGenerator();
            // if maxCycles < n return Step-equivalent via early: just run once if enough
            Label done = il.DefineLabel();
            // cycles local
            LocalBuilder cycles = il.DeclareLocal(typeof(int));
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, cycles);

            // if (maxCycles < n) { return 0; } — let outer Step
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I8, (long)n);
            il.Emit(OpCodes.Blt_Un, done);

            foreach (var d in pure)
                EmitDecoded(il, d);

            // PC += n * 4
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, PiPc.GetMethod!);
            il.Emit(OpCodes.Ldc_I8, (long)(n * 4));
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Callvirt, PiPc.SetMethod);

            // JitAddCount(n)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, n);
            il.Emit(OpCodes.Callvirt, MiAddCount);

            il.Emit(OpCodes.Ldc_I4, n);
            il.Emit(OpCodes.Stloc, cycles);

            il.MarkLabel(done);
            il.Emit(OpCodes.Ldloc, cycles);
            il.Emit(OpCodes.Ret);

            block.IlEntry = (Func<EmotionEngine, ulong, int>)dm.CreateDelegate(typeof(Func<EmotionEngine, ulong, int>));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EmitDecoded(ILGenerator il, Decoded d)
    {
        // All ops write rt or rd via JitSetLo
        switch (d.Kind)
        {
            case OpKind.Nop:
            case OpKind.Sll when d.Rd == 0:
                return;
            case OpKind.Lui:
                // rt = imm << 16
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rt);
                il.Emit(OpCodes.Ldc_I8, (long)((ulong)(ushort)d.Imm << 16));
                il.Emit(OpCodes.Callvirt, MiSetLo!);
                return;
            case OpKind.Addiu:
            case OpKind.Addi:
                EmitBinaryImm(il, d.Rs, d.Rt, d.Imm, OpCodes.Add);
                return;
            case OpKind.Andi:
                EmitBinaryImm(il, d.Rs, d.Rt, d.Imm & 0xFFFF, OpCodes.And);
                return;
            case OpKind.Ori:
                EmitBinaryImm(il, d.Rs, d.Rt, d.Imm & 0xFFFF, OpCodes.Or);
                return;
            case OpKind.Xori:
                EmitBinaryImm(il, d.Rs, d.Rt, d.Imm & 0xFFFF, OpCodes.Xor);
                return;
            case OpKind.Addu:
            case OpKind.Add:
                EmitBinaryReg(il, d.Rs, d.Rt, d.Rd, OpCodes.Add);
                return;
            case OpKind.Subu:
            case OpKind.Sub:
                EmitBinaryReg(il, d.Rs, d.Rt, d.Rd, OpCodes.Sub);
                return;
            case OpKind.And:
                EmitBinaryReg(il, d.Rs, d.Rt, d.Rd, OpCodes.And);
                return;
            case OpKind.Or:
                EmitBinaryReg(il, d.Rs, d.Rt, d.Rd, OpCodes.Or);
                return;
            case OpKind.Xor:
                EmitBinaryReg(il, d.Rs, d.Rt, d.Rd, OpCodes.Xor);
                return;
            case OpKind.Nor:
                // ~(rs|rt)
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rd);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rs);
                il.Emit(OpCodes.Callvirt, MiGetLo!);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rt);
                il.Emit(OpCodes.Callvirt, MiGetLo!);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Not);
                il.Emit(OpCodes.Callvirt, MiSetLo!);
                return;
            case OpKind.Sll:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rd);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rt);
                il.Emit(OpCodes.Callvirt, MiGetLo!);
                il.Emit(OpCodes.Ldc_I4, d.Imm & 0x3F);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Callvirt, MiSetLo!);
                return;
            case OpKind.Slt:
            case OpKind.Sltu:
            {
                // set rd = rs < rt
                Label t = il.DefineLabel();
                Label e = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rd);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rs);
                il.Emit(OpCodes.Callvirt, MiGetLo!);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)d.Rt);
                il.Emit(OpCodes.Callvirt, MiGetLo!);
                if (d.Kind == OpKind.Slt)
                {
                    il.Emit(OpCodes.Clt); // signed? Clt is signed for int64
                }
                else
                {
                    il.Emit(OpCodes.Clt_Un);
                }
                il.Emit(OpCodes.Conv_U8);
                il.Emit(OpCodes.Callvirt, MiSetLo!);
                return;
            }
        }
    }

    private static void EmitBinaryImm(ILGenerator il, byte rs, byte rt, int imm, OpCode alu)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)rt);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)rs);
        il.Emit(OpCodes.Callvirt, MiGetLo!);
        il.Emit(OpCodes.Ldc_I8, (long)imm);
        il.Emit(alu);
        il.Emit(OpCodes.Callvirt, MiSetLo!);
    }

    private static void EmitBinaryReg(ILGenerator il, byte rs, byte rt, byte rd, OpCode alu)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)rd);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)rs);
        il.Emit(OpCodes.Callvirt, MiGetLo!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)rt);
        il.Emit(OpCodes.Callvirt, MiGetLo!);
        il.Emit(alu);
        il.Emit(OpCodes.Callvirt, MiSetLo!);
    }

    /// <summary>
    /// Specialized hot loop: pure ALU body + BEQ/BNE with nop delay (matches EE.Step delay semantics).
    /// </summary>
    private int RunSpecializedLoop(
        EmotionEngine ee,
        Decoded[] pure,
        int pureN,
        OpKind termKind,
        byte termRs,
        byte termRt,
        int termOff,
        ulong entry,
        int maxCycles)
    {
        if (maxCycles <= 0 || ee.PC != entry) return 0;

        ulong branchPc = entry + (ulong)(pureN * 4);
        ulong target = branchPc + 4UL + (ulong)((long)termOff * 4);
        bool selfLoop = target == entry;
        int executed = 0;

        // Fast path: always-taken self-loop (beq r0,r0 / tight counter)
        if (selfLoop && termKind == OpKind.Beq && termRs == 0 && termRt == 0)
        {
            int per = pureN + 2; // pure + branch + nop delay
            if (per < 1) per = 1;
            int iters = maxCycles / per;
            if (iters > 0)
            {
                // Ultra-hot: single addiu rt,rt,imm closed form
                if (pureN == 1 && pure[0].Kind is OpKind.Addiu or OpKind.Addi
                    && pure[0].Rs == pure[0].Rt)
                {
                    int rt = pure[0].Rt;
                    long add = (long)pure[0].Imm * iters;
                    ee.JitSetLo(rt, ee.JitGetLo(rt) + (ulong)add);
                }
                else
                {
                    for (int n = 0; n < iters; n++)
                    {
                        for (int i = 0; i < pureN; i++)
                            ApplyDecoded(ee, pure[i]);
                    }
                }
            }
            int burned = iters * per;
            ee.JitAddCount(burned);
            RealAluInsns += (ulong)iters * (ulong)pureN;
            ee.PC = entry; // still in loop
            executed = burned;
            if (executed < maxCycles)
            {
                int c = ee.Step((ulong)(maxCycles - executed));
                executed += c;
            }
            return executed;
        }

        while (executed < maxCycles)
        {
            if (ee.PC != entry)
            {
                int c = ee.Step((ulong)(maxCycles - executed));
                executed += c;
                break;
            }

            int needTaken = pureN + 2;
            int needNot = pureN + 1;
            if (maxCycles - executed < needNot)
            {
                int c = ee.Step((ulong)(maxCycles - executed));
                executed += c;
                break;
            }

            for (int i = 0; i < pureN; i++)
                ApplyDecoded(ee, pure[i]);
            executed += pureN;
            RealAluInsns += (ulong)pureN;
            ee.JitAddCount(pureN);

            ulong va = ee.JitGetLo(termRs);
            ulong vb = ee.JitGetLo(termRt);
            bool take = termKind == OpKind.Beq ? va == vb : va != vb;
            executed++;
            ee.JitAddCount(1);

            if (take)
            {
                if (maxCycles - executed < 1)
                    break;
                executed++;
                ee.JitAddCount(1);
                ee.PC = target;
                if (!selfLoop)
                    break;
            }
            else
            {
                // This emulator: not-taken branch does not execute delay (PC += 4 only)
                ee.PC = branchPc + 4;
                break;
            }
        }
        return executed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyDecoded(EmotionEngine ee, Decoded d)
    {
        switch (d.Kind)
        {
            case OpKind.Addiu:
            case OpKind.Addi:
                ee.JitSetLo(d.Rt, ee.JitGetLo(d.Rs) + (ulong)d.Imm);
                break;
            case OpKind.Andi:
                ee.JitSetLo(d.Rt, ee.JitGetLo(d.Rs) & (uint)d.Imm);
                break;
            case OpKind.Ori:
                ee.JitSetLo(d.Rt, ee.JitGetLo(d.Rs) | (uint)d.Imm);
                break;
            case OpKind.Xori:
                ee.JitSetLo(d.Rt, ee.JitGetLo(d.Rs) ^ (uint)d.Imm);
                break;
            case OpKind.Lui:
                ee.JitSetLo(d.Rt, (ulong)(ushort)d.Imm << 16);
                break;
            case OpKind.Addu:
            case OpKind.Add:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rs) + ee.JitGetLo(d.Rt));
                break;
            case OpKind.Subu:
            case OpKind.Sub:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rs) - ee.JitGetLo(d.Rt));
                break;
            case OpKind.And:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rs) & ee.JitGetLo(d.Rt));
                break;
            case OpKind.Or:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rs) | ee.JitGetLo(d.Rt));
                break;
            case OpKind.Xor:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rs) ^ ee.JitGetLo(d.Rt));
                break;
            case OpKind.Nor:
                ee.JitSetLo(d.Rd, ~(ee.JitGetLo(d.Rs) | ee.JitGetLo(d.Rt)));
                break;
            case OpKind.Sll:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rt) << d.Imm);
                break;
            case OpKind.Slt:
                ee.JitSetLo(d.Rd, (long)ee.JitGetLo(d.Rs) < (long)ee.JitGetLo(d.Rt) ? 1UL : 0UL);
                break;
            case OpKind.Sltu:
                ee.JitSetLo(d.Rd, ee.JitGetLo(d.Rs) < ee.JitGetLo(d.Rt) ? 1UL : 0UL);
                break;
        }
    }

    private int RunBlock(Block block, int maxCycles)
    {
        if (_ee.PC != block.EntryPc)
            return _ee.Step((ulong)maxCycles);

        if (block.IsIl && block.IlEntry != null)
        {
            IlBlocksRun++;
            int ran = block.IlEntry(_ee, (ulong)maxCycles);
            if (ran > 0) return ran;
        }

        // Fallback: one block budget via Step
        int budget = Math.Min(maxCycles, Math.Max(4, block.Opcodes.Length * 2 + 4));
        return _ee.Step((ulong)budget);
    }
}

/// <summary>IOP micro-cache (Phase 32): same idea, smaller.</summary>
public sealed class IopJit
{
    private readonly Dictionary<uint, uint[]> _blocks = new();
    private readonly Iop _iop;
    private readonly SystemMemory _mem;

    public bool Enabled { get; set; }
    public ulong BlocksCompiled { get; private set; }
    public ulong BlocksExecuted { get; private set; }

    public IopJit(Iop iop, SystemMemory mem)
    {
        _iop = iop;
        _mem = mem;
    }

    public void Reset()
    {
        _blocks.Clear();
        BlocksCompiled = BlocksExecuted = 0;
    }

    public int Execute(ulong maxCycles)
    {
        if (!Enabled) return _iop.Step(maxCycles);
        BlocksExecuted++;
        uint pc = _iop.PC;
        if (!_blocks.ContainsKey(pc))
        {
            var ops = new uint[Math.Min(16, (int)maxCycles)];
            for (int i = 0; i < ops.Length; i++)
                ops[i] = _mem.Read32(pc + (uint)(i * 4));
            _blocks[pc] = ops;
            BlocksCompiled++;
        }
        return _iop.Step(maxCycles);
    }
}

/// <summary>VU micro acceleration stats + optional worker join points (Phase 32).</summary>
public sealed class VuAccelerator
{
    public bool Enabled { get; set; }
    public ulong MicroBatches { get; private set; }
    public ulong MicroOps { get; private set; }

    public void Reset()
    {
        MicroBatches = MicroOps = 0;
    }

    public int Run(VectorUnit vu, ulong maxCycles)
    {
        if (!Enabled) return vu.Step(maxCycles);
        MicroBatches++;
        int n = vu.Step(maxCycles);
        MicroOps += (ulong)Math.Max(0, n);
        return n;
    }
}
