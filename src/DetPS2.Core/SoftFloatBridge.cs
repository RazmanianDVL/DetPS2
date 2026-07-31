using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Shared host IEEE bridge for EE software multi-precision doubles.
///
/// The Emotion Engine COP1 is single-precision only; retail titles ship libm-class
/// soft-double (mul/add/sub/sin/cos + float↔double) as integer multi-precision. On the
/// interpreter that costs 10k–100k+ cycles per sin and stalls table-fill loops
/// (Haven SLUS_205.17 @ 0x0010CCD8: <c>for i: table[i]=(float)sin(i*k)</c>) past 100–250M
/// with no FILEIO/DLL.DAT progress.
///
/// Titles register known entry PCs via <see cref="Register"/>. When EE PC hits a
/// registered entry the host evaluates the IEEE op and returns via <c>$ra</c> — bit-exact
/// for finite float↔double and mul/add/sub; sin/cos use host libm (correct math for
/// table fills; not a bit-match of the guest poly). Disabled when no entries registered.
/// </summary>
public static class SoftFloatBridge
{
    public enum Op : byte
    {
        /// <summary>v0 = a0 * a1 (IEEE double bits in GPRs).</summary>
        DMul = 1,
        /// <summary>v0 = a0 + a1.</summary>
        DAdd = 2,
        /// <summary>v0 = a0 - a1.</summary>
        DSub = 3,
        /// <summary>v0 = a0 / a1.</summary>
        DDiv = 4,
        /// <summary>v0 = sin(a0).</summary>
        DSin = 5,
        /// <summary>v0 = cos(a0).</summary>
        DCos = 6,
        /// <summary>v0 = (double)f12 — SN/o32 float arg in COP1 f12.</summary>
        F32ToF64 = 7,
        /// <summary>f0 = (float)a0 — result in COP1 f0.</summary>
        F64ToF32 = 8,
        /// <summary>v0 = sqrt(a0).</summary>
        DSqrt = 9,
    }

    private static readonly Dictionary<uint, Op> _entries = new();
    private static int _hits;

    public static int Hits => _hits;
    public static int EntryCount => _entries.Count;
    public static bool Active => _entries.Count > 0;

    public static void Reset()
    {
        _entries.Clear();
        _hits = 0;
    }

    public static void Register(uint entryPc, Op op)
    {
        _entries[entryPc & 0x1FFFFFFFu] = op;
    }

    public static void RegisterMany(IEnumerable<(uint pc, Op op)> entries)
    {
        foreach (var (pc, op) in entries)
            Register(pc, op);
    }

    /// <summary>
    /// If PC is a registered soft-float entry, evaluate on host and return via $ra.
    /// Returns true when accelerated (caller should count 1 cycle and continue).
    /// </summary>
    public static bool TryFastPath(EmotionEngine ee)
    {
        if (_entries.Count == 0) return false;
        uint pc = (uint)(ee.PC & 0x1FFFFFFFu);
        if (!_entries.TryGetValue(pc, out Op op)) return false;

        ulong result = 0;
        bool writeV0 = true;
        switch (op)
        {
            case Op.DMul:
                result = DBits(FromBits(ee.GetGpr(4).Lo) * FromBits(ee.GetGpr(5).Lo));
                break;
            case Op.DAdd:
                result = DBits(FromBits(ee.GetGpr(4).Lo) + FromBits(ee.GetGpr(5).Lo));
                break;
            case Op.DSub:
                result = DBits(FromBits(ee.GetGpr(4).Lo) - FromBits(ee.GetGpr(5).Lo));
                break;
            case Op.DDiv:
            {
                double b = FromBits(ee.GetGpr(5).Lo);
                result = DBits(b != 0.0 ? FromBits(ee.GetGpr(4).Lo) / b : double.PositiveInfinity);
                break;
            }
            case Op.DSin:
                result = DBits(Math.Sin(FromBits(ee.GetGpr(4).Lo)));
                break;
            case Op.DCos:
                result = DBits(Math.Cos(FromBits(ee.GetGpr(4).Lo)));
                break;
            case Op.DSqrt:
            {
                double a = FromBits(ee.GetGpr(4).Lo);
                result = DBits(a >= 0.0 ? Math.Sqrt(a) : double.NaN);
                break;
            }
            case Op.F32ToF64:
                // f12 is the MIPS o32 soft-float / COP1 argument register.
                result = DBits((double)ee.GetFpr(12));
                break;
            case Op.F64ToF32:
                ee.SetFpr(0, (float)FromBits(ee.GetGpr(4).Lo));
                writeV0 = false;
                break;
            default:
                return false;
        }

        if (writeV0)
            ee.SetGpr(2, new EmotionEngine.Gpr128 { Lo = result });

        // Return like jr ra (no delay slot — we replace the whole callee).
        ulong ra = ee.GetGpr(31).Lo;
        if (ra == 0)
            return false; // refuse to jump to null; let guest run
        ee.PC = ra;
        _hits++;
        return true;
    }

    private static double FromBits(ulong bits) => BitConverter.UInt64BitsToDouble(bits);
    private static ulong DBits(double d) => BitConverter.DoubleToUInt64Bits(d);
}
