using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpuTopologyProbe;

internal static class NativeTopologyReader
{
    private const int CpuSetMinimumSize = 32;
    private const int LogicalProcessorHeaderSize = 8;
    private const int ProcessorRelationshipOffset = 8;
    private const int ProcessorRelationshipGroupCountOffset = ProcessorRelationshipOffset + 22;
    private const int ProcessorRelationshipGroupMasksOffset = ProcessorRelationshipOffset + 24;
    private const int ProcessorPowerInformationSize = 24;

    internal static List<CpuSetRecord> ReadCpuSets()
    {
        uint length = 0;
        bool initialResult = NativeMethods.GetSystemCpuSetInformation(IntPtr.Zero, 0, out length, IntPtr.Zero, 0);
        int initialError = Marshal.GetLastWin32Error();
        if (initialResult || length == 0 || initialError != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new Win32Exception(initialError, "Could not determine the SYSTEM_CPU_SET_INFORMATION buffer size.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!NativeMethods.GetSystemCpuSetInformation(buffer, length, out uint returnedLength, IntPtr.Zero, 0))
            {
                throw NativeMethods.LastError(nameof(NativeMethods.GetSystemCpuSetInformation));
            }

            var records = new List<CpuSetRecord>();
            int offset = 0;
            while (offset < returnedLength)
            {
                IntPtr record = IntPtr.Add(buffer, offset);
                int size = Marshal.ReadInt32(record, 0);
                int type = Marshal.ReadInt32(record, 4);
                if (size < LogicalProcessorHeaderSize || offset + size > returnedLength)
                {
                    throw new InvalidDataException($"Invalid SYSTEM_CPU_SET_INFORMATION record size {size} at offset {offset}.");
                }

                if (type == 0 && size >= CpuSetMinimumSize)
                {
                    records.Add(new CpuSetRecord
                    {
                        Id = unchecked((uint)Marshal.ReadInt32(record, 8)),
                        Group = unchecked((ushort)Marshal.ReadInt16(record, 12)),
                        LogicalProcessorIndex = Marshal.ReadByte(record, 14),
                        CoreIndex = Marshal.ReadByte(record, 15),
                        LastLevelCacheIndex = Marshal.ReadByte(record, 16),
                        NumaNodeIndex = Marshal.ReadByte(record, 17),
                        EfficiencyClass = Marshal.ReadByte(record, 18),
                        Flags = Marshal.ReadByte(record, 19),
                        SchedulingClass = Marshal.ReadByte(record, 20)
                    });
                }

                offset += size;
            }

            return records;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static List<ProcessorCoreRecord> ReadProcessorCores()
    {
        uint length = 0;
        bool initialResult = NativeMethods.GetLogicalProcessorInformationEx(
            NativeMethods.RelationProcessorCore,
            IntPtr.Zero,
            ref length);
        int initialError = Marshal.GetLastWin32Error();
        if (initialResult || length == 0 || initialError != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new Win32Exception(initialError, "Could not determine the processor-core information buffer size.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(
                    NativeMethods.RelationProcessorCore,
                    buffer,
                    ref length))
            {
                throw NativeMethods.LastError(nameof(NativeMethods.GetLogicalProcessorInformationEx));
            }

            var records = new List<ProcessorCoreRecord>();
            int groupAffinitySize = IntPtr.Size + sizeof(ushort) * 4;
            int offset = 0;
            while (offset < length)
            {
                IntPtr record = IntPtr.Add(buffer, offset);
                int relationship = Marshal.ReadInt32(record, 0);
                int size = Marshal.ReadInt32(record, 4);
                if (size < LogicalProcessorHeaderSize || offset + size > length)
                {
                    throw new InvalidDataException($"Invalid SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record size {size} at offset {offset}.");
                }

                if (relationship == NativeMethods.RelationProcessorCore && size >= ProcessorRelationshipGroupMasksOffset)
                {
                    ushort groupCount = unchecked((ushort)Marshal.ReadInt16(record, ProcessorRelationshipGroupCountOffset));
                    int requiredSize = ProcessorRelationshipGroupMasksOffset + groupCount * groupAffinitySize;
                    if (requiredSize > size)
                    {
                        throw new InvalidDataException($"Processor-core record at offset {offset} contains an invalid group count {groupCount}.");
                    }

                    var core = new ProcessorCoreRecord
                    {
                        PhysicalCoreNumber = records.Count,
                        Flags = Marshal.ReadByte(record, ProcessorRelationshipOffset),
                        EfficiencyClass = Marshal.ReadByte(record, ProcessorRelationshipOffset + 1)
                    };

                    for (int index = 0; index < groupCount; index++)
                    {
                        IntPtr groupMask = IntPtr.Add(record, ProcessorRelationshipGroupMasksOffset + index * groupAffinitySize);
                        ulong mask = IntPtr.Size == 8
                            ? unchecked((ulong)Marshal.ReadInt64(groupMask, 0))
                            : unchecked((uint)Marshal.ReadInt32(groupMask, 0));
                        ushort group = unchecked((ushort)Marshal.ReadInt16(groupMask, IntPtr.Size));
                        core.GroupMasks.Add(new NativeGroupMask(group, mask));
                    }

                    records.Add(core);
                }

                offset += size;
            }

            return records;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static List<ProcessorPowerSample> ReadProcessorPowerInformation(int processorCount)
    {
        if (processorCount <= 0)
        {
            return new List<ProcessorPowerSample>();
        }

        int byteLength = checked(processorCount * ProcessorPowerInformationSize);
        IntPtr buffer = Marshal.AllocHGlobal(byteLength);
        try
        {
            uint status = NativeMethods.CallNtPowerInformation(
                NativeMethods.ProcessorInformation,
                IntPtr.Zero,
                0,
                buffer,
                checked((uint)byteLength));
            if (status != 0)
            {
                throw new InvalidOperationException($"CallNtPowerInformation returned NTSTATUS 0x{status:X8}.");
            }

            var samples = new List<ProcessorPowerSample>(processorCount);
            for (int index = 0; index < processorCount; index++)
            {
                IntPtr sample = IntPtr.Add(buffer, index * ProcessorPowerInformationSize);
                samples.Add(new ProcessorPowerSample
                {
                    SourceProcessorNumber = ReadUInt32(sample, 0),
                    MaxMhz = ReadUInt32(sample, 4),
                    CurrentMhz = ReadUInt32(sample, 8),
                    MhzLimit = ReadUInt32(sample, 12),
                    MaxIdleState = ReadUInt32(sample, 16),
                    CurrentIdleState = ReadUInt32(sample, 20)
                });
            }

            return samples;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static List<ProcessorKey> EnumerateActiveProcessors()
    {
        var processors = new List<ProcessorKey>();
        ushort groupCount = NativeMethods.GetActiveProcessorGroupCount();
        for (ushort group = 0; group < groupCount; group++)
        {
            uint count = NativeMethods.GetActiveProcessorCount(group);
            for (uint index = 0; index < count && index <= byte.MaxValue; index++)
            {
                processors.Add(new ProcessorKey(group, checked((byte)index)));
            }
        }

        return processors;
    }

    private static uint ReadUInt32(IntPtr pointer, int offset)
    {
        return unchecked((uint)Marshal.ReadInt32(pointer, offset));
    }
}
