using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpuTopologyProbe;

internal static class NativeMethods
{
    internal const ushort AllProcessorGroups = 0xffff;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int RelationProcessorCore = 0;
    internal const int ProcessorInformation = 11;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    [DllImport("kernel32.dll")]
    internal static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    internal static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadGroupAffinity(
        IntPtr thread,
        ref GroupAffinity groupAffinity,
        out GroupAffinity previousGroupAffinity);

    [DllImport("powrprof.dll")]
    internal static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint acValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint dcValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupAffinity
    {
        internal UIntPtr Mask;
        internal ushort Group;
        internal ushort Reserved0;
        internal ushort Reserved1;
        internal ushort Reserved2;
    }

    internal static Win32Exception LastError(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }
}
