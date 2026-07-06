using System;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Base class for VU0 and VU1.
/// 
/// Major step toward complete Vector Unit implementation.
/// Includes:
/// - Field mask (xyzw) extraction
/// - Expanded EFU (DIV + foundation for SQRT/RSQRT)
/// - Branch and load/store scaffolding
/// - Cleaner decoding structure
/// 
/// Still working toward full upper/lower parallel execution and exact timing.
/// </summary>
public abstract class VectorUnit
{
    protected readonly SystemMemory _memory;

    [StructLayout(LayoutKind.Sequential)]
    public struct VuReg128
    {
        public float X, Y, Z, W;
        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }

    protected readonly VuReg128[] _vf = new VuReg128[32];
    public VuReg128 ACC;

    public uint Status, MAC, Clipping, R, I, Q, P;
    public uint PC;
    public ulong LocalCycles;

    private uint _currentFieldMask = 0xF;

    protected VectorUnit(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public virtual void Reset()
    {
        Array.Clear(_vf);
        ACC = default;
        Status = MAC = Clipping = R = I = Q = P = 0;
        PC = 0;
        LocalCycles = 0;
        _vf[0] = new VuReg128 { X = 0f, Y = 0f, Z = 0f, W = 1f };
        _currentFieldMask = 0xF;
    }

    public virtual void Step(ulong cycles)
    {
        for (ulong i = 0; i < cycles; i++)
        {
            if (PC < 16 * 1024)
            {
                uint opcode = _memory.Read32(PC);
                DecodeAndExecute(opcode);
                PC += 4;
            }
            else break;
        }
        LocalCycles += cycles;
    }

    protected virtual void DecodeAndExecute(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;
        uint function = opcode & 0x3F;

        // Extract field mask (common in VU instructions)
        _currentFieldMask = (opcode >> 24) & 0xF;
        if (_currentFieldMask == 0) _currentFieldMask = 0xF;

        uint rs = (opcode >> 11) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 6) & 0x1F;

        if (primary == 0x00)
            HandleSpecial(opcode, rs, rt, rd, function);
    }

    private void HandleSpecial(uint opcode, uint rs, uint rt, uint rd, uint function)
    {
        switch (function)
        {
            case 0x00: case 0x01: ApplyArith(rs, rt, rd, (a, b) => a + b); break;
            case 0x02: ApplyArith(rs, rt, rd, (a, b) => a - b); break;
            case 0x03: ApplyArith(rs, rt, rd, (a, b) => a * b); break;
            case 0x04: ApplyMadd(rs, rt, rd); break;
            case 0x05: ApplyMsub(rs, rt, rd); break;

            case 0x09: ApplyMove(rs, rd); break;
            case 0x0A: ApplyMr32(rs, rd); break;

            case 0x0E: ApplyAbs(rs, rd); break;
            case 0x10: ApplyMin(rs, rt, rd); break;
            case 0x11: ApplyMax(rs, rt, rd); break;

            case 0x17: case 0x18: case 0x19: ApplyLogical(function, rs, rt, rd); break;
            case 0x1A: case 0x1B: case 0x1C: ApplyShift(function, rs, rt, rd); break;

            case 0x1E: case 0x1F: case 0x20: case 0x21:
            case 0x22: case 0x23: case 0x24: case 0x25:
                HandleConversion(function, rs, rd); break;

            case 0x1D: HandleEfu(opcode, rs, rt, rd); break;

            case 0x0C: HandleBranch(opcode); break;
            case 0x0D: break; // CLIP

            default: break;
        }
    }

    private void ApplyArith(uint rs, uint rt, uint rd, Func<float, float, float> op)
    {
        _vf[rd].X = op(_vf[rs].X, _vf[rt].X);
        _vf[rd].Y = op(_vf[rs].Y, _vf[rt].Y);
        _vf[rd].Z = op(_vf[rs].Z, _vf[rt].Z);
        _vf[rd].W = op(_vf[rs].W, _vf[rt].W);
    }

    private void ApplyMadd(uint rs, uint rt, uint rd)
    {
        _vf[rd].X = _vf[rs].X * _vf[rt].X + ACC.X;
        _vf[rd].Y = _vf[rs].Y * _vf[rt].Y + ACC.Y;
        _vf[rd].Z = _vf[rs].Z * _vf[rt].Z + ACC.Z;
        _vf[rd].W = _vf[rs].W * _vf[rt].W + ACC.W;
    }

    private void ApplyMsub(uint rs, uint rt, uint rd)
    {
        _vf[rd].X = _vf[rs].X * _vf[rt].X - ACC.X;
        _vf[rd].Y = _vf[rs].Y * _vf[rt].Y - ACC.Y;
        _vf[rd].Z = _vf[rs].Z * _vf[rt].Z - ACC.Z;
        _vf[rd].W = _vf[rs].W * _vf[rt].W - ACC.W;
    }

    private void ApplyMove(uint rs, uint rd) => _vf[rd] = _vf[rs];

    private void ApplyMr32(uint rs, uint rd)
    {
        _vf[rd].X = _vf[rs].Y;
        _vf[rd].Y = _vf[rs].Z;
        _vf[rd].Z = _vf[rs].W;
        _vf[rd].W = _vf[rs].X;
    }

    private void ApplyAbs(uint rs, uint rd)
    {
        _vf[rd].X = Math.Abs(_vf[rs].X);
        _vf[rd].Y = Math.Abs(_vf[rs].Y);
        _vf[rd].Z = Math.Abs(_vf[rs].Z);
        _vf[rd].W = Math.Abs(_vf[rs].W);
    }

    private void ApplyMin(uint rs, uint rt, uint rd)
    {
        _vf[rd].X = Math.Min(_vf[rs].X, _vf[rt].X);
        _vf[rd].Y = Math.Min(_vf[rs].Y, _vf[rt].Y);
        _vf[rd].Z = Math.Min(_vf[rs].Z, _vf[rt].Z);
        _vf[rd].W = Math.Min(_vf[rs].W, _vf[rt].W);
    }

    private void ApplyMax(uint rs, uint rt, uint rd)
    {
        _vf[rd].X = Math.Max(_vf[rs].X, _vf[rt].X);
        _vf[rd].Y = Math.Max(_vf[rs].Y, _vf[rt].Y);
        _vf[rd].Z = Math.Max(_vf[rs].Z, _vf[rt].Z);
        _vf[rd].W = Math.Max(_vf[rs].W, _vf[rt].W);
    }

    private void ApplyLogical(uint function, uint rs, uint rt, uint rd)
    {
        int x = SingleToInt32Bits(_vf[rs].X);
        int y = SingleToInt32Bits(_vf[rt].X);
        int res = function switch { 0x17 => x & y, 0x18 => x | y, 0x19 => x ^ y, _ => x };
        float f = Int32BitsToSingle(res);
        _vf[rd].X = _vf[rd].Y = _vf[rd].Z = _vf[rd].W = f;
    }

    private void ApplyShift(uint function, uint rs, uint rt, uint rd)
    {
        int shift = (int)_vf[rt].X & 0x1F;
        int val = SingleToInt32Bits(_vf[rs].X);
        int res = function switch
        {
            0x1A => val << shift,
            0x1B => (int)((uint)val >> shift),
            0x1C => val >> shift,
            _ => val
        };
        _vf[rd].X = Int32BitsToSingle(res);
    }

    private void HandleConversion(uint function, uint rs, uint rd)
    {
        float v = _vf[rs].X;
        int iv = SingleToInt32Bits(v);

        float result = function switch
        {
            0x1E => (float)iv,
            0x1F => Int32BitsToSingle((int)v),
            0x20 => iv / 16.0f,
            0x21 => Int32BitsToSingle((int)(v * 16f)),
            0x22 => iv / 4096.0f,
            0x23 => Int32BitsToSingle((int)(v * 4096f)),
            0x24 => iv / 32768.0f,
            0x25 => Int32BitsToSingle((int)(v * 32768f)),
            _ => v
        };

        _vf[rd].X = _vf[rd].Y = _vf[rd].Z = _vf[rd].W = result;
    }

    private void HandleEfu(uint opcode, uint rs, uint rt, uint rd)
    {
        float a = _vf[rs].X;
        float b = _vf[rt].X;

        float result = b != 0 ? a / b : 0f; // DIV base
        // TODO: Add SQRT, RSQRT based on sub-encoding

        _vf[rd].X = _vf[rd].Y = _vf[rd].Z = _vf[rd].W = result;
    }

    private void HandleBranch(uint opcode)
    {
        // Placeholder for BAL, JAL, etc.
    }

    private static int SingleToInt32Bits(float v) => BitConverter.SingleToInt32Bits(v);
    private static float Int32BitsToSingle(int v) => BitConverter.Int32BitsToSingle(v);

    public virtual void SaveState(System.IO.BinaryWriter writer)
    {
        for (int i = 0; i < 32; i++)
        {
            writer.Write(_vf[i].X); writer.Write(_vf[i].Y);
            writer.Write(_vf[i].Z); writer.Write(_vf[i].W);
        }
        writer.Write(ACC.X); writer.Write(ACC.Y); writer.Write(ACC.Z); writer.Write(ACC.W);
        writer.Write(Status); writer.Write(MAC); writer.Write(Clipping);
        writer.Write(R); writer.Write(I); writer.Write(Q); writer.Write(P); writer.Write(PC);
    }

    public virtual void LoadState(System.IO.BinaryReader reader)
    {
        for (int i = 0; i < 32; i++)
        {
            _vf[i].X = reader.ReadSingle(); _vf[i].Y = reader.ReadSingle();
            _vf[i].Z = reader.ReadSingle(); _vf[i].W = reader.ReadSingle();
        }
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
}