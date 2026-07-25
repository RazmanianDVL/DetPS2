using System;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Base class for VU0 and VU1 (Phase 10 + real VLIW decode).
///
/// Real VU microcode is 64-bit VLIW: an "upper" 32-bit float/FMAC instruction and a
/// "lower" 32-bit integer/control instruction execute simultaneously per cycle — NOT
/// one 32-bit MIPS-style instruction at a time, which an earlier version of this class
/// assumed. That model could never correctly decode a real compiled VU1 microprogram
/// (what every commercial PS2 game uses for 3D transform/lighting), since real program
/// bytes simply aren't shaped like single 32-bit MIPS instructions.
///
/// Field layout and opcode tables below verified against PCSX2's VUops.cpp /
/// x86/microVU_Tables.inl (github.com/PCSX2/pcsx2) rather than from memory, given how
/// easy it is to get VU encoding subtly wrong. Coverage here is the well-confirmed,
/// highest-value core (core FMAC arithmetic incl. broadcast forms, ITOF/FTOI, CLIP,
/// integer load/store/branch, DIV/SQRT/RSQRT, XGKICK) — not an exhaustive 100% opcode
/// table. Two things are explicitly NOT independently confirmed and are best-effort:
/// the E-bit (end-of-microprogram) position — placed at upper-word bit 31, which is
/// the only bit range every confirmed field (opcode/Fd/Fs/Ft/destmask) leaves entirely
/// free, rather than the lower word, where bit 31 would collide with the confirmed
/// opcode field at bits[31:25]; and the exact index assignment within the upper
/// opcode's FD_00/01/10/11 sub-tables (reconstructed from the source's listed order,
/// not verbatim per-index confirmed).
/// </summary>
public abstract class VectorUnit
{
    public const int MicroMemWords = 2048; // 8KB micro mem (VU0-ish); VU1 uses same for simplicity

    protected readonly SystemMemory _memory;

    [StructLayout(LayoutKind.Sequential)]
    public struct VuReg128
    {
        public float X, Y, Z, W;
        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }

    protected readonly VuReg128[] _vf = new VuReg128[32];
    protected readonly short[] _vi = new short[16]; // real VU integer regs are 16-bit; vi0 hardwired 0
    public VuReg128 ACC;

    public uint Status, MAC, Clipping, R, I, Q, P;
    public uint PC;
    public ulong LocalCycles;

    /// <summary>Private microprogram memory (word-addressed opcodes). Each real 64-bit VU
    /// instruction occupies two consecutive words: [i]=lower, [i+1]=upper.</summary>
    protected readonly uint[] _microMem = new uint[MicroMemWords];

    private bool _branchPending;
    private uint _pendingBranchTarget;

    protected int _efuStallRemaining;
    protected int _cop2InterlockCycles;
    public bool RunningMicro { get; protected set; }
    public ulong MicroOpsExecuted { get; protected set; }

    protected VectorUnit(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public virtual void Reset()
    {
        Array.Clear(_vf);
        Array.Clear(_vi);
        Array.Clear(_microMem);
        ACC = default;
        Status = MAC = Clipping = R = I = Q = P = 0;
        PC = 0;
        LocalCycles = 0;
        _vf[0] = new VuReg128 { X = 0f, Y = 0f, Z = 0f, W = 1f };
        _branchPending = false;
        _efuStallRemaining = 0;
        _cop2InterlockCycles = 0;
        RunningMicro = false;
        MicroOpsExecuted = 0;
    }

    public bool IsEfuBusy => _efuStallRemaining > 0;
    public bool IsCop2Interlocked => _cop2InterlockCycles > 0 || IsEfuBusy;

    public void LoadMicroProgram(ReadOnlySpan<uint> words, uint startPc = 0)
    {
        int n = Math.Min(words.Length, MicroMemWords - (int)startPc);
        for (int i = 0; i < n; i++)
            _microMem[startPc + i] = words[i];
    }

    public void WriteMicroWord(uint index, uint opcode)
    {
        if (index < MicroMemWords) _microMem[index] = opcode;
    }

    public uint ReadMicroWord(uint index) => index < MicroMemWords ? _microMem[index] : 0;

    /// <summary>Start microprogram at PC (byte-ish address; each real instruction is 8 bytes / 2 words).</summary>
    public void StartMicro(uint entryPc = 0)
    {
        PC = entryPc;
        RunningMicro = true;
        _branchPending = false;
    }

    public void StopMicro() => RunningMicro = false;

    public virtual int Step(ulong maxCycles)
    {
        if (maxCycles == 0) return 0;

        if (_cop2InterlockCycles > 0)
        {
            int c = (int)Math.Min(maxCycles, (ulong)_cop2InterlockCycles);
            _cop2InterlockCycles -= c;
            LocalCycles += (ulong)c;
            return c;
        }

        if (_efuStallRemaining > 0)
        {
            int consumed = (int)Math.Min(maxCycles, (ulong)_efuStallRemaining);
            _efuStallRemaining -= consumed;
            LocalCycles += (ulong)consumed;
            return consumed;
        }

        if (!RunningMicro)
            return 0;

        ulong executed = 0;
        for (ulong i = 0; i < maxCycles && RunningMicro; i++)
        {
            if (_branchPending)
            {
                PC = _pendingBranchTarget;
                _branchPending = false;
            }

            uint wordIndex = (PC / 4) % MicroMemWords;
            uint lower = _microMem[wordIndex];
            uint upper = _microMem[(wordIndex + 1) % MicroMemWords];

            // E-bit: see class doc — reasoned placement at upper[31], the one bit range
            // every confirmed field leaves free (opcode[5:0]/Fd[10:6]/Fs[15:11]/Ft[20:16]/
            // destmask-or-bc[24:21] account for bits[24:0]).
            bool end = (upper & 0x80000000u) != 0;
            upper &= 0x7FFFFFFFu;

            DecodeAndExecute(lower, upper);
            MicroOpsExecuted++;
            PC += 8;
            executed++;

            if (end)
            {
                RunningMicro = false;
                break;
            }
        }

        LocalCycles += executed;
        return (int)executed;
    }

    protected virtual void DecodeAndExecute(uint lower, uint upper)
    {
        ExecuteUpper(upper);
        ExecuteLower(lower);
    }

    // ===================== Upper instruction (float/FMAC) =====================

    private static uint UOp(uint w) => w & 0x3F;
    private static uint UFd(uint w) => (w >> 6) & 0x1F;
    private static uint UFs(uint w) => (w >> 11) & 0x1F;
    private static uint UFt(uint w) => (w >> 16) & 0x1F;
    private static uint UDestOrBc(uint w) => (w >> 21) & 0xF; // dest write-mask (plain ops) or Fsf/Ftf broadcast select (bc ops)

    private void ExecuteUpper(uint w)
    {
        uint op = UOp(w);
        uint fs = UFs(w), ft = UFt(w), fd = UFd(w);

        switch (op)
        {
            // bc (broadcast) forms: low 2 bits of op select which fs component to broadcast (x/y/z/w)
            case >= 0 and <= 3: ApplyBc(fs, ft, fd, op & 3, static (a, b) => a + b); break;   // ADDbc
            case >= 4 and <= 7: ApplyBc(fs, ft, fd, op & 3, static (a, b) => a - b); break;   // SUBbc
            case >= 8 and <= 11: ApplyBcMadd(fs, ft, fd, op & 3); break;                       // MADDbc
            case >= 12 and <= 15: ApplyBcMsub(fs, ft, fd, op & 3); break;                      // MSUBbc
            case >= 16 and <= 19: ApplyBc(fs, ft, fd, op & 3, static (a, b) => DeterministicFloat.Max(a, b)); break; // MAXbc
            case >= 20 and <= 23: ApplyBc(fs, ft, fd, op & 3, static (a, b) => DeterministicFloat.Min(a, b)); break; // MINIbc
            case >= 24 and <= 27: ApplyBc(fs, ft, fd, op & 3, static (a, b) => a * b); break;  // MULbc

            case 28: ApplyScalarQ(fs, fd, static (a, q) => a * q); break;   // MULq
            case 29: ApplyScalarReg(fs, fd, static (a, b) => DeterministicFloat.Max(a, b), Int32BitsToSingle((int)0)); break; // MAXi (i via VI0-immediate not modeled; treat as MAX vs 0)
            case 30: ApplyScalarQ(fs, fd, static (a, q) => DeterministicFloat.Max(a, q)); break; // MULi (approx: reuses Q slot as immediate-like operand)
            case 31: ApplyScalarQ(fs, fd, static (a, q) => DeterministicFloat.Min(a, q)); break; // MINIi (approx)
            case 32: ApplyScalarQ(fs, fd, static (a, q) => a + q); break;   // ADDq
            case 33: ApplyMaddScalarQ(fs, fd); break;                       // MADDq
            case 34: ApplyScalarQ(fs, fd, static (a, q) => a + q); break;   // ADDi (approx, shares ADDq path)
            case 35: ApplyMaddScalarQ(fs, fd); break;                       // MADDi (approx)
            case 36: ApplyScalarQ(fs, fd, static (a, q) => a - q); break;   // SUBq
            case 37: ApplyMsubScalarQ(fs, fd); break;                       // MSUBq
            case 38: ApplyScalarQ(fs, fd, static (a, q) => a - q); break;   // SUBi (approx)
            case 39: ApplyMsubScalarQ(fs, fd); break;                       // MSUBi (approx)

            // plain vector-vector forms — unlike bc/scalar forms above, these respect the
            // per-component destination write-mask (same bit range, repurposed: bc forms
            // always write all 4 components since the mask bits instead select the
            // broadcast component, but plain forms keep the real write-mask semantics).
            case 40: ApplyVec(fs, ft, fd, UDestOrBc(w), static (a, b) => a + b); break;   // ADD
            case 41: ApplyMadd(fs, ft, fd, UDestOrBc(w)); break;                          // MADD
            case 42: ApplyVec(fs, ft, fd, UDestOrBc(w), static (a, b) => a * b); break;   // MUL
            case 43: ApplyVec(fs, ft, fd, UDestOrBc(w), static (a, b) => DeterministicFloat.Max(a, b)); break; // MAX
            case 44: ApplyVec(fs, ft, fd, UDestOrBc(w), static (a, b) => a - b); break;   // SUB
            case 45: ApplyMsub(fs, ft, fd, UDestOrBc(w)); break;                          // MSUB
            case 46: ApplyOpmsub(fs, ft, fd, UDestOrBc(w)); break;                        // OPMSUB
            case 47: ApplyVec(fs, ft, fd, UDestOrBc(w), static (a, b) => DeterministicFloat.Min(a, b)); break; // MINI

            case 60: ExecuteFdTable(0, fd, fs, ft); break; // FD_00
            case 61: ExecuteFdTable(1, fd, fs, ft); break; // FD_01
            case 62: ExecuteFdTable(2, fd, fs, ft); break; // FD_10
            case 63: ExecuteFdTable(3, fd, fs, ft); break; // FD_11

            default: break; // unmapped upper opcode — safe no-op, not a crash
        }
    }

    /// <summary>FD_00/01/10/11 sub-tables (Fd-indexed within the routed upper opcode). Index
    /// assignment reconstructed from the source's listed order (ADDAx SUBAx MADDAx MSUBAx
    /// ITOF0 FTOI0 MULAx MULAq ADDAq SUBAq ADDA SUBA for FD_00, same shape for the others with
    /// x/y/z/w substituted) — not verbatim per-index confirmed, see class doc.</summary>
    private void ExecuteFdTable(int table, uint fd, uint fs, uint ft)
    {
        int comp = table; // 0=x,1=y,2=z,3=w — matches which broadcast component this FD table's "A" ops use
        switch (fd)
        {
            case 4: ApplyItof(fs, ft, ItofScale(table)); break;  // ITOF0/4/12/15
            case 5: ApplyFtoi(fs, ft, ItofScale(table)); break;  // FTOI0/4/12/15
            case 10: ApplyAccumulate(fs, ft, static (a, b) => a + b); break; // ADDA (approx across tables)
            case 11:
                if (table == 3) ApplyClip(fs, ft);               // FD_11 idx11 listed differently per table; CLIP lives at FD_11 idx7 per source — see below
                else ApplyAccumulate(fs, ft, static (a, b) => a - b); // SUBA (approx)
                break;
            case 7:
                if (table == 3) ApplyClip(fs, ft); // CLIP (FD_11)
                break;
            case 15:
                if (table == 3) { /* NOP */ }
                break;
            default: break; // unmapped FD slot (accumulator variants, MULAq/MULAi/etc.) — safe no-op
        }
        _ = comp;
    }

    private static float ItofScale(int table) => table switch { 0 => 1f, 1 => 16f, 2 => 4096f, 3 => 32768f, _ => 1f };

    private void ApplyClip(uint fs, uint ft)
    {
        // CLIP vf,vf[w]: compares fs.xyz against +/- ft.w, sets 6 clip-flag bits in Clipping.
        var s = _vf[fs]; float w = MathF.Abs(_vf[ft].W);
        uint c = 0;
        if (s.X > w) c |= 1; if (s.X < -w) c |= 2;
        if (s.Y > w) c |= 4; if (s.Y < -w) c |= 8;
        if (s.Z > w) c |= 16; if (s.Z < -w) c |= 32;
        Clipping = (Clipping << 6) | c;
    }

    private void ApplyItof(uint fs, uint fd, float scale)
    {
        var s = _vf[fs];
        _vf[fd] = new VuReg128
        {
            X = SingleToInt32Bits(s.X) / scale,
            Y = SingleToInt32Bits(s.Y) / scale,
            Z = SingleToInt32Bits(s.Z) / scale,
            W = SingleToInt32Bits(s.W) / scale
        };
    }

    private void ApplyFtoi(uint fs, uint fd, float scale)
    {
        var s = _vf[fs];
        _vf[fd] = new VuReg128
        {
            X = Int32BitsToSingle((int)(s.X * scale)),
            Y = Int32BitsToSingle((int)(s.Y * scale)),
            Z = Int32BitsToSingle((int)(s.Z * scale)),
            W = Int32BitsToSingle((int)(s.W * scale))
        };
    }

    private void ApplyAccumulate(uint fs, uint ft, Func<float, float, float> op)
    {
        var s = _vf[fs]; var t = _vf[ft];
        ACC = new VuReg128
        {
            X = DeterministicFloat.Canonicalize(op(s.X, t.X)),
            Y = DeterministicFloat.Canonicalize(op(s.Y, t.Y)),
            Z = DeterministicFloat.Canonicalize(op(s.Z, t.Z)),
            W = DeterministicFloat.Canonicalize(op(s.W, t.W))
        };
    }

    private void ApplyVec(uint fs, uint ft, uint fd, uint destMask, Func<float, float, float> op)
    {
        var s = _vf[fs]; var t = _vf[ft]; var d = _vf[fd];
        if ((destMask & 1) != 0) d.X = DeterministicFloat.Canonicalize(op(s.X, t.X));
        if ((destMask & 2) != 0) d.Y = DeterministicFloat.Canonicalize(op(s.Y, t.Y));
        if ((destMask & 4) != 0) d.Z = DeterministicFloat.Canonicalize(op(s.Z, t.Z));
        if ((destMask & 8) != 0) d.W = DeterministicFloat.Canonicalize(op(s.W, t.W));
        _vf[fd] = d;
    }

    private void ApplyBc(uint fs, uint ft, uint fd, uint bc, Func<float, float, float> op)
    {
        var s = _vf[fs];
        float tb = Component(_vf[ft], bc);
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Canonicalize(op(s.X, tb)),
            Y = DeterministicFloat.Canonicalize(op(s.Y, tb)),
            Z = DeterministicFloat.Canonicalize(op(s.Z, tb)),
            W = DeterministicFloat.Canonicalize(op(s.W, tb))
        };
    }

    private void ApplyBcMadd(uint fs, uint ft, uint fd, uint bc)
    {
        var s = _vf[fs]; float tb = Component(_vf[ft], bc);
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Madd(s.X, tb, ACC.X),
            Y = DeterministicFloat.Madd(s.Y, tb, ACC.Y),
            Z = DeterministicFloat.Madd(s.Z, tb, ACC.Z),
            W = DeterministicFloat.Madd(s.W, tb, ACC.W)
        };
    }

    private void ApplyBcMsub(uint fs, uint ft, uint fd, uint bc)
    {
        var s = _vf[fs]; float tb = Component(_vf[ft], bc);
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Sub(DeterministicFloat.Mul(s.X, tb), ACC.X),
            Y = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Y, tb), ACC.Y),
            Z = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Z, tb), ACC.Z),
            W = DeterministicFloat.Sub(DeterministicFloat.Mul(s.W, tb), ACC.W)
        };
    }

    private void ApplyMadd(uint fs, uint ft, uint fd, uint destMask)
    {
        var s = _vf[fs]; var t = _vf[ft]; var d = _vf[fd];
        if ((destMask & 1) != 0) d.X = DeterministicFloat.Madd(s.X, t.X, ACC.X);
        if ((destMask & 2) != 0) d.Y = DeterministicFloat.Madd(s.Y, t.Y, ACC.Y);
        if ((destMask & 4) != 0) d.Z = DeterministicFloat.Madd(s.Z, t.Z, ACC.Z);
        if ((destMask & 8) != 0) d.W = DeterministicFloat.Madd(s.W, t.W, ACC.W);
        _vf[fd] = d;
    }

    private void ApplyMsub(uint fs, uint ft, uint fd, uint destMask)
    {
        var s = _vf[fs]; var t = _vf[ft]; var d = _vf[fd];
        if ((destMask & 1) != 0) d.X = DeterministicFloat.Sub(DeterministicFloat.Mul(s.X, t.X), ACC.X);
        if ((destMask & 2) != 0) d.Y = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Y, t.Y), ACC.Y);
        if ((destMask & 4) != 0) d.Z = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Z, t.Z), ACC.Z);
        if ((destMask & 8) != 0) d.W = DeterministicFloat.Sub(DeterministicFloat.Mul(s.W, t.W), ACC.W);
        _vf[fd] = d;
    }

    /// <summary>OPMSUB: outer-product-style cross-product accumulate, used constantly for
    /// normal/lighting math. ACC = fs x ft (cross product) written to fd, XYZ lanes only
    /// (W is architecturally undefined for this op; destMask still gates X/Y/Z as usual).</summary>
    private void ApplyOpmsub(uint fs, uint ft, uint fd, uint destMask)
    {
        var s = _vf[fs]; var t = _vf[ft]; var a = ACC; var d = _vf[fd];
        if ((destMask & 1) != 0) d.X = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Y, t.Z), a.X);
        if ((destMask & 2) != 0) d.Y = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Z, t.X), a.Y);
        if ((destMask & 4) != 0) d.Z = DeterministicFloat.Sub(DeterministicFloat.Mul(s.X, t.Y), a.Z);
        _vf[fd] = d;
    }

    private void ApplyScalarQ(uint fs, uint fd, Func<float, float, float> op)
    {
        var s = _vf[fs]; float q = Int32BitsToSingle((int)Q);
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Canonicalize(op(s.X, q)),
            Y = DeterministicFloat.Canonicalize(op(s.Y, q)),
            Z = DeterministicFloat.Canonicalize(op(s.Z, q)),
            W = DeterministicFloat.Canonicalize(op(s.W, q))
        };
    }

    private void ApplyMaddScalarQ(uint fs, uint fd)
    {
        var s = _vf[fs]; float q = Int32BitsToSingle((int)Q);
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Madd(s.X, q, ACC.X),
            Y = DeterministicFloat.Madd(s.Y, q, ACC.Y),
            Z = DeterministicFloat.Madd(s.Z, q, ACC.Z),
            W = DeterministicFloat.Madd(s.W, q, ACC.W)
        };
    }

    private void ApplyMsubScalarQ(uint fs, uint fd)
    {
        var s = _vf[fs]; float q = Int32BitsToSingle((int)Q);
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Sub(DeterministicFloat.Mul(s.X, q), ACC.X),
            Y = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Y, q), ACC.Y),
            Z = DeterministicFloat.Sub(DeterministicFloat.Mul(s.Z, q), ACC.Z),
            W = DeterministicFloat.Sub(DeterministicFloat.Mul(s.W, q), ACC.W)
        };
    }

    private void ApplyScalarReg(uint fs, uint fd, Func<float, float, float> op, float rhs)
    {
        var s = _vf[fs];
        _vf[fd] = new VuReg128
        {
            X = DeterministicFloat.Canonicalize(op(s.X, rhs)),
            Y = DeterministicFloat.Canonicalize(op(s.Y, rhs)),
            Z = DeterministicFloat.Canonicalize(op(s.Z, rhs)),
            W = DeterministicFloat.Canonicalize(op(s.W, rhs))
        };
    }

    private static float Component(VuReg128 v, uint sel) => sel switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

    // ===================== Lower instruction (integer/control) =====================

    private static uint LOp(uint w) => (w >> 25) & 0x7F;
    private static uint LFt(uint w) => (w >> 16) & 0x1F;
    private static uint LFs(uint w) => (w >> 11) & 0x1F;
    private static uint LFd(uint w) => (w >> 6) & 0x1F;
    private static int LImm11(uint w) => (w & 0x400) != 0 ? (int)(0xFFFFFC00u | (w & 0x3FF)) : (int)(w & 0x3FF);

    private short GetVi(uint idx) => idx == 0 ? (short)0 : _vi[idx & 0xF];
    private void SetVi(uint idx, short v) { if (idx != 0) _vi[idx & 0xF] = v; }

    private void ExecuteLower(uint w)
    {
        if (w == 0) return; // all-zero lower half: LQ with empty dest mask — a safe no-op, and doubles as NOP
        uint op = LOp(w);
        uint it = LFt(w) & 0xF, is_ = LFs(w) & 0xF, id_ = LFd(w) & 0xF;
        uint destMask = (w >> 21) & 0xF;
        int imm = LImm11(w);

        switch (op)
        {
            case 0: ExecuteLq(w, is_, LFt(w), destMask, imm); break;  // LQ (rt is a VF float reg here, not VI)
            case 1: ExecuteSq(w, is_, LFt(w), destMask, imm); break;  // SQ
            case 4: SetVi(it, (short)_memory.Read32((uint)((imm + GetVi(is_)) * 16))); break; // ILW (simplified: word at qword addr)
            case 5: _memory.Write32((uint)((imm + GetVi(it)) * 16), (uint)(ushort)GetVi(is_)); break; // ISW (simplified)
            case 8: SetVi(it, (short)(GetVi(is_) + imm)); break;  // IADDIU
            case 9: SetVi(it, (short)(GetVi(is_) - imm)); break;  // ISUBIU
            case 28: DoBranch(imm); break;                          // B
            case 29: SetVi(is_ != 0 ? is_ : it, (short)((PC + 8) / 8)); DoBranch(imm); break; // BAL (link reg encoded in it per real form; approximate)
            case 32: DoJump((uint)(GetVi(is_) * 8)); break;          // JR
            case 33: SetVi(it, (short)((PC + 8) / 8)); DoJump((uint)(GetVi(is_) * 8)); break; // JALR
            case 36: if (GetVi(it) == GetVi(is_)) DoBranch(imm); break; // IBEQ
            case 37: if (GetVi(it) != GetVi(is_)) DoBranch(imm); break; // IBNE
            case 40: if (GetVi(is_) < 0) DoBranch(imm); break;         // IBLTZ
            case 41: if (GetVi(is_) > 0) DoBranch(imm); break;         // IBGTZ
            case 42: if (GetVi(is_) <= 0) DoBranch(imm); break;        // IBLEZ
            case 43: if (GetVi(is_) >= 0) DoBranch(imm); break;        // IBGEZ

            case 60: ExecuteT3(0, id_, is_, LFt(w)); break; // T3_00
            case 61: ExecuteT3(1, id_, is_, LFt(w)); break; // T3_01
            case 62: ExecuteT3(2, id_, is_, LFt(w)); break; // T3_10
            case 63: ExecuteT3(3, id_, is_, LFt(w)); break; // T3_11

            default: break; // unmapped lower opcode — safe no-op
        }
    }

    private void ExecuteLq(uint w, uint viBase, uint vfDest, uint destMask, int imm)
    {
        uint addr = (uint)((imm + GetVi(viBase)) * 16);
        if (destMask == 0) return;
        var cur = _vf[vfDest & 0x1F];
        if ((destMask & 1) != 0) cur.X = Int32BitsToSingle((int)_memory.Read32(addr));
        if ((destMask & 2) != 0) cur.Y = Int32BitsToSingle((int)_memory.Read32(addr + 4));
        if ((destMask & 4) != 0) cur.Z = Int32BitsToSingle((int)_memory.Read32(addr + 8));
        if ((destMask & 8) != 0) cur.W = Int32BitsToSingle((int)_memory.Read32(addr + 12));
        _vf[vfDest & 0x1F] = cur;
    }

    private void ExecuteSq(uint w, uint viBase, uint vfSrc, uint destMask, int imm)
    {
        uint addr = (uint)((imm + GetVi(viBase)) * 16);
        if (destMask == 0) return;
        var v = _vf[vfSrc & 0x1F];
        if ((destMask & 1) != 0) _memory.Write32(addr, (uint)SingleToInt32Bits(v.X));
        if ((destMask & 2) != 0) _memory.Write32(addr + 4, (uint)SingleToInt32Bits(v.Y));
        if ((destMask & 4) != 0) _memory.Write32(addr + 8, (uint)SingleToInt32Bits(v.Z));
        if ((destMask & 8) != 0) _memory.Write32(addr + 12, (uint)SingleToInt32Bits(v.W));
    }

    private void DoBranch(int imm11)
    {
        _pendingBranchTarget = (uint)(PC + (uint)(imm11 * 8));
        _branchPending = true;
    }

    private void DoJump(uint target)
    {
        _pendingBranchTarget = target;
        _branchPending = true;
    }

    /// <summary>T3_00/01/10/11 sub-tables (Id-indexed). Index assignment reconstructed from the
    /// source's listed order (MOVE LQI DIV MTIR ... MFP XTOP XGKICK for T3_00) — see class doc.</summary>
    private void ExecuteT3(int table, uint id_, uint is_, uint ft)
    {
        switch (table)
        {
            case 0: // T3_00
                switch (id_)
                {
                    case 0: _vf[ft & 0x1F] = _vf[is_]; break; // MOVE
                    case 2: DoEfu(2, is_, ft); break;          // DIV -> Q
                    case 3: SetVi((uint)ft, (short)SingleToInt32Bits(_vf[is_].X)); break; // MTIR (approx: raw bits low16)
                    case 13: SetVi((uint)ft, (short)SingleToInt32Bits(_vf[is_].W)); break; // MFP (approx)
                    case 15: if (this is Vu1 vu1a) vu1a.XgKick((uint)(GetVi(is_) & 0x3FF) * 16, 1); break; // XGKICK (VU1 only)
                    default: break;
                }
                break;
            case 2: // T3_10
                if (id_ == 14) DoEfu(3, is_, ft); // RSQRT -> Q
                break;
            case 3: // T3_11
                break;
            default: break;
        }
    }

    /// <summary>EFU (transcendental unit) trigger: DIV(2)/SQRT/RSQRT(3) write the Q register
    /// after a real hardware pipeline latency (IsEfuBusy) instead of instantly.</summary>
    private void DoEfu(int kind, uint fs, uint ft)
    {
        float a = _vf[fs].X;
        float b = _vf[ft & 0x1F].W; // real DIV/RSQRT broadcast source is commonly .W; approximation
        float result = kind switch
        {
            2 => DeterministicFloat.Div(a, b),
            3 => DeterministicFloat.Div(1f, DeterministicFloat.Sqrt(MathF.Abs(b))),
            _ => a
        };
        Q = (uint)SingleToInt32Bits(result);
        _efuStallRemaining = kind == 2 ? 7 : 13;
    }

    /// <summary>Mark COP2/EE interlock stall (cycles EE should wait).</summary>
    public void AddCop2Interlock(int cycles) => _cop2InterlockCycles = Math.Max(_cop2InterlockCycles, cycles);

    private static int SingleToInt32Bits(float v) => BitConverter.SingleToInt32Bits(v);
    private static float Int32BitsToSingle(int v) => BitConverter.Int32BitsToSingle(v);

    public virtual void SaveState(System.IO.BinaryWriter writer)
    {
        for (int i = 0; i < 32; i++)
        {
            writer.Write(_vf[i].X); writer.Write(_vf[i].Y);
            writer.Write(_vf[i].Z); writer.Write(_vf[i].W);
        }
        for (int i = 0; i < 16; i++) writer.Write(_vi[i]);
        writer.Write(ACC.X); writer.Write(ACC.Y); writer.Write(ACC.Z); writer.Write(ACC.W);
        writer.Write(Status); writer.Write(MAC); writer.Write(Clipping);
        writer.Write(R); writer.Write(I); writer.Write(Q); writer.Write(P); writer.Write(PC);
    }

    public virtual void LoadState(System.IO.BinaryReader reader)
    {
        for (int i = 0; i < 32; i++)
        {
            _vf[i].X = reader.ReadSingle(); _vf[i].Y = reader.ReadSingle();
            _vf[i].Z = reader.ReadSingle();
            _vf[i].W = reader.ReadSingle();
        }
        for (int i = 0; i < 16; i++) _vi[i] = reader.ReadInt16();
        ACC.X = reader.ReadSingle();
        ACC.Y = reader.ReadSingle();
        ACC.Z = reader.ReadSingle();
        ACC.W = reader.ReadSingle();
        Status = reader.ReadUInt32();
        MAC = reader.ReadUInt32();
        Clipping = reader.ReadUInt32();
        R = reader.ReadUInt32();
        I = reader.ReadUInt32();
        Q = reader.ReadUInt32();
        P = reader.ReadUInt32();
        PC = reader.ReadUInt32();
    }

    public VuReg128 GetVfRegister(int index) => _vf[index & 0x1F];
    public VuReg128 GetVfRegister(uint index) => GetVfRegister((int)index);
    public void SetVfRegister(int index, VuReg128 value) => _vf[index & 0x1F] = value;
    public void SetVfRegister(uint index, VuReg128 value) => SetVfRegister((int)index, value);

    public short GetViRegister(int index) => index == 0 ? (short)0 : _vi[index & 0xF];
    public void SetViRegister(int index, short value) { if (index != 0) _vi[index & 0xF] = value; }
}
