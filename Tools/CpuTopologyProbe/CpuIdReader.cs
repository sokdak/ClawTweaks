using System.Runtime.Intrinsics.X86;

namespace CpuTopologyProbe;

internal static class CpuIdReader
{
    internal static bool IsSupported => X86Base.IsSupported && Environment.Is64BitProcess;

    internal static CpuIdSnapshot Read(ushort group, byte logicalProcessorIndex)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("CPUID probing requires a 64-bit x86 process.");
        }

        if (logicalProcessorIndex >= 64)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorIndex));
        }

        var targetAffinity = new NativeMethods.GroupAffinity
        {
            Group = group,
            Mask = new UIntPtr(1UL << logicalProcessorIndex)
        };

        IntPtr thread = NativeMethods.GetCurrentThread();
        if (!NativeMethods.SetThreadGroupAffinity(thread, ref targetAffinity, out NativeMethods.GroupAffinity previousAffinity))
        {
            throw NativeMethods.LastError(nameof(NativeMethods.SetThreadGroupAffinity));
        }

        try
        {
            return ReadCurrentProcessor();
        }
        finally
        {
            if (!NativeMethods.SetThreadGroupAffinity(thread, ref previousAffinity, out _))
            {
                throw NativeMethods.LastError("restoring thread group affinity");
            }
        }
    }

    private static CpuIdSnapshot ReadCurrentProcessor()
    {
        var leafZero = X86Base.CpuId(0, 0);
        uint maxBasicLeaf = unchecked((uint)leafZero.Item1);
        var snapshot = new CpuIdSnapshot
        {
            MaxBasicLeaf = maxBasicLeaf
        };

        if (maxBasicLeaf >= 7)
        {
            var structuredFeatures = X86Base.CpuId(7, 0);
            snapshot.HybridFeatureFlag = (unchecked((uint)structuredFeatures.Item4) & (1u << 15)) != 0;
        }

        if (maxBasicLeaf >= 0x1a)
        {
            var hybridInformation = X86Base.CpuId(0x1a, 0);
            uint eax = unchecked((uint)hybridInformation.Item1);
            byte coreType = checked((byte)(eax >> 24));
            snapshot.HybridCoreTypeCode = $"0x{coreType:X2}";
            snapshot.HybridCoreTypeName = CoreTypeName(coreType);
            snapshot.NativeModelId = eax & 0x00ff_ffff;
        }

        uint? topologyLeaf = SelectTopologyLeaf(maxBasicLeaf);
        if (topologyLeaf.HasValue)
        {
            snapshot.TopologyLeaf = $"0x{topologyLeaf.Value:X}";
            ReadTopologyLevels(snapshot, topologyLeaf.Value);
            DeriveTopologyIds(snapshot);
        }

        return snapshot;
    }

    private static uint? SelectTopologyLeaf(uint maxBasicLeaf)
    {
        if (maxBasicLeaf >= 0x1f && HasTopologyLevels(0x1f))
        {
            return 0x1f;
        }

        if (maxBasicLeaf >= 0x0b && HasTopologyLevels(0x0b))
        {
            return 0x0b;
        }

        return null;
    }

    private static bool HasTopologyLevels(int leaf)
    {
        var firstLevel = X86Base.CpuId(leaf, 0);
        return (unchecked((uint)firstLevel.Item2) & 0xffff) != 0;
    }

    private static void ReadTopologyLevels(CpuIdSnapshot snapshot, uint leaf)
    {
        for (int subLeaf = 0; subLeaf < 32; subLeaf++)
        {
            var result = X86Base.CpuId(checked((int)leaf), subLeaf);
            uint eax = unchecked((uint)result.Item1);
            uint ebx = unchecked((uint)result.Item2);
            uint ecx = unchecked((uint)result.Item3);
            uint edx = unchecked((uint)result.Item4);
            ushort logicalProcessors = checked((ushort)(ebx & 0xffff));
            byte levelType = checked((byte)((ecx >> 8) & 0xff));
            if (logicalProcessors == 0 || levelType == 0)
            {
                break;
            }

            snapshot.X2ApicId = edx;
            snapshot.TopologyLevels.Add(new CpuIdTopologyLevel
            {
                SubLeaf = subLeaf,
                LevelNumber = checked((byte)(ecx & 0xff)),
                LevelType = levelType,
                LevelTypeName = TopologyLevelTypeName(levelType),
                Shift = checked((byte)(eax & 0x1f)),
                LogicalProcessorsAtLevel = logicalProcessors,
                X2ApicId = edx
            });
        }
    }

    private static void DeriveTopologyIds(CpuIdSnapshot snapshot)
    {
        if (!snapshot.X2ApicId.HasValue || snapshot.TopologyLevels.Count == 0)
        {
            return;
        }

        uint x2ApicId = snapshot.X2ApicId.Value;
        byte smtShift = FindShift(snapshot, 1) ?? 0;
        byte? coreShift = FindShift(snapshot, 2);
        byte? moduleShift = FindShift(snapshot, 3);
        byte? dieShift = FindShift(snapshot, 5);
        byte highestShift = snapshot.TopologyLevels.Max(level => level.Shift);

        snapshot.SmtId = ExtractId(x2ApicId, 0, smtShift);
        if (coreShift.HasValue)
        {
            snapshot.CoreId = ExtractId(x2ApicId, smtShift, coreShift.Value);
        }

        if (moduleShift.HasValue)
        {
            byte lowerShift = coreShift ?? smtShift;
            snapshot.ModuleId = ExtractId(x2ApicId, lowerShift, moduleShift.Value);
        }

        if (dieShift.HasValue)
        {
            byte lowerShift = moduleShift ?? coreShift ?? smtShift;
            snapshot.DieId = ExtractId(x2ApicId, lowerShift, dieShift.Value);
        }

        snapshot.PackageId = highestShift >= 32 ? 0 : x2ApicId >> highestShift;
    }

    private static byte? FindShift(CpuIdSnapshot snapshot, byte levelType)
    {
        return snapshot.TopologyLevels
            .Where(level => level.LevelType == levelType)
            .Select(level => (byte?)level.Shift)
            .FirstOrDefault();
    }

    private static uint ExtractId(uint value, byte lowerShift, byte upperShift)
    {
        if (upperShift <= lowerShift)
        {
            return 0;
        }

        int width = upperShift - lowerShift;
        uint mask = width >= 32 ? uint.MaxValue : (1u << width) - 1;
        return (value >> lowerShift) & mask;
    }

    private static string CoreTypeName(byte coreType)
    {
        return coreType switch
        {
            0x40 => "Core",
            0x20 => "Atom",
            0x00 => "NotReported",
            _ => "Unknown"
        };
    }

    private static string TopologyLevelTypeName(byte levelType)
    {
        return levelType switch
        {
            1 => "Smt",
            2 => "Core",
            3 => "Module",
            4 => "Tile",
            5 => "Die",
            6 => "DieGroup",
            _ => "Unknown"
        };
    }
}
