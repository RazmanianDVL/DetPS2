using System;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Base class for VU0 and VU1.
/// Phase 6 progress - more instructions added.
/// </summary>
public abstract class VectorUnit
{
    protected readonly SystemMemory _memory;

    [StructLayout(LayoutKind.Sequential)]
    public struct VuReg128
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }

    protected readonly VuReg128[] _vf = new VuReg128[32];

    public VuReg128 ACC;

    public uint Status;
    public uint MAC;
    public uint Clipping;
    public uint R;
    public uint I;
    public uint Q;
    public uint P;

    public uint PC;
    public ulong LocalCycles;

    protected VectorUnit(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public virtual void Reset()
    {
        Array.Clear(_vf);
        ACC = default;
        Status = 0;
        MAC = 0;
        Clipping = 0;
        R = 0;
        I = 0;
        Q = 0;
        P = 0;
        PC = 0;
        LocalCycles = 0;

        _vf[0] = new VuReg128 { X = 0f, Y = 0f, Z = 0f, W = 1f };
    }

    public virtual void Step(ulong cycles)
    {
        for (ulong i = 0; i < cycles; i++)
        {
            if (PC < _memory.Size)
            {
                uint opcode = _memory.Read32(PC);
                ExecuteInstruction(opcode);
                PC += 4;
            }
            else
            {
                break;
            }
        }
        LocalCycles += cycles;
    }

    protected virtual void ExecuteInstruction(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;
        uint function = opcode & 0x3F;

        uint rs = (opcode >> 11) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 6) & 0x1F;

        switch (primary)
        {
            case 0x00:
                HandleSpecial(opcode, rs, rt, rd);
                break;

            default:
                break;
        }
    }

    private void HandleSpecial(uint opcode, uint rs, uint rt, uint rd)
    {
        uint function = opcode & 0x3F;

        switch (function)
        {
            case 0x00: // ADD
                _vf[rd].X = _vf[rs].X + _vf[rt].X;
                _vf[rd].Y = _vf[rs].Y + _vf[rt].Y;
                _vf[rd].Z = _vf[rs].Z + _vf[rt].Z;
                _vf[rd].W = _vf[rs].W + _vf[rt].W;
                break;

            case 0x01: // ADDI
                float imm = (short)(opcode & 0xFFFF);
                _vf[rt].X = _vf[rs].X + imm;
                _vf[rt].Y = _vf[rs].Y + imm;
                _vf[rt].Z = _vf[rs].Z + imm;
                _vf[rt].W = _vf[rs].W + imm;
                break;

            case 0x02: // SUB
                _vf[rd].X = _vf[rs].X - _vf[rt].X;
                _vf[rd].Y = _vf[rs].Y - _vf[rt].Y;
                _vf[rd].Z = _vf[rs].Z - _vf[rt].Z;
                _vf[rd].W = _vf[rs].W - _vf[rt].W;
                break;

            case 0x03: // MUL
                _vf[rd].X = _vf[rs].X * _vf[rt].X;
                _vf[rd].Y = _vf[rs].Y * _vf[rt].Y;
                _vf[rd].Z = _vf[rs].Z * _vf[rt].Z;
                _vf[rd].W = _vf[rs].W * _vf[rt].W;
                break;

            case 0x04: // MADD
                _vf[rd].X = _vf[rs].X * _vf[rt].X + ACC.X;
                _vf[rd].Y = _vf[rs].Y * _vf[rt].Y + ACC.Y;
                _vf[rd].Z = _vf[rs].Z * _vf[rt].Z + ACC.Z;
                _vf[rd].W = _vf[rs].W * _vf[rt].W + ACC.W;
                break;

            case 0x05: // MSUB
                _vf[rd].X = _vf[rs].X * _vf[rt].X - ACC.X;
                _vf[rd].Y = _vf[rs].Y * _vf[rt].Y - ACC.Y;
                _vf[rd].Z = _vf[rs].Z * _vf[rt].Z - ACC.Z;
                _vf[rd].W = _vf[rs].W * _vf[rt].W - ACC.W;
                break;

            case 0x09: // MOVE
                _vf[rd] = _vf[rs];
                break;

            case 0x0A: // MR32
                _vf[rd].X = _vf[rs].Y;
                _vf[rd].Y = _vf[rs].Z;
                _vf[rd].Z = _vf[rs].W;
                _vf[rd].W = _vf[rs].X;
                break;

            case 0x0E: // ABS
                _vf[rd].X = Math.Abs(_vf[rs].X);
                _vf[rd].Y = Math.Abs(_vf[rs].Y);
                _vf[rd].Z = Math.Abs(_vf[rs].Z);
                _vf[rd].W = Math.Abs(_vf[rs].W);
                break;

            case 0x10: // MIN
                _vf[rd].X = Math.Min(_vf[rs].X, _vf[rt].X);
                _vf[rd].Y = Math.Min(_vf[rs].Y, _vf[rt].Y);
                _vf[rd].Z = Math.Min(_vf[rs].Z, _vf[rt].Z);
                _vf[rd].W = Math.Min(_vf[rs].W, _vf[rt].W);
                break;

            case 0x11: // MAX
                _vf[rd].X = Math.Max(_vf[rs].X, _vf[rt].X);
                _vf[rd].Y = Math.Max(_vf[rs].Y, _vf[rt].Y);
                _vf[rd].Z = Math.Max(_vf[rs].Z, _vf[rt].Z);
                _vf[rd].W = Math.Max(_vf[rs].W, _vf[rt].W);
                break;

            case 0x12: // EQ
                break;

            case 0x13: // LT
                break;

            case 0x14: // LE
                break;

            case 0x15: // GT
                break;

            case 0x16: // GE
                break;

            default:
                break;
        }
    }

    public virtual void SaveState(System.IO.BinaryWriter writer)
    {
        for (int i = 0; i < 32; i++)
        {
            writer.Write(_vf[i].X);
            writer.Write(_vf[i].Y);
            writer.Write(_vf[i].Z);
            writer.Write(_vf[i].W);
        }
        writer.Write(ACC.X);
        writer.Write(ACC.Y);
        writer.Write(ACC.Z);
        writer.Write(ACC.W);
        writer.Write(Status);
        writer.Write(MAC);
        writer.Write(Clipping);
        writer.Write(R);
        writer.Write(I);
        writer.Write(Q);
        writer.Write(P);
        writer.Write(PC);
    }

    public virtual void LoadState(System.IO.BinaryReader reader)
    {
        for (int i = 0; i < 32; i++)
        {
            _vf[i].X = reader.ReadSingle();
            _vf[i].Y = reader.ReadSingle();
            _vf[i].Z = reader.ReadSingle();
            _vf[i].W = reader.ReadSingle();
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
