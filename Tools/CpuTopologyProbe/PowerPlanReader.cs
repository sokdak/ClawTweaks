using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CpuTopologyProbe;

internal static class PowerPlanReader
{
    private const int LoadProbeDurationMilliseconds = 1250;
    private static readonly Guid ProcessorSettingsSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly Guid[] FrequencyLimitSettings =
    {
        new("75b0ae3f-bce0-45a7-8c89-c9611c25e100"),
        new("75b0ae3f-bce0-45a7-8c89-c9611c25e101"),
        new("75b0ae3f-bce0-45a7-8c89-c9611c25e102")
    };

    internal static PowerPlanReport Read()
    {
        var report = new PowerPlanReport();
        uint activeSchemeStatus = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeSchemePointer);
        if (activeSchemeStatus != 0 || activeSchemePointer == IntPtr.Zero)
        {
            report.Status = $"PowerGetActiveSchemeFailed:{activeSchemeStatus}";
            return report;
        }

        try
        {
            report.ActiveScheme = Marshal.PtrToStructure<Guid>(activeSchemePointer);
        }
        finally
        {
            NativeMethods.LocalFree(activeSchemePointer);
        }

        try
        {
            report.EffectiveOverlayReadStatus = NativeMethods.PowerGetEffectiveOverlayScheme(out Guid overlay);
            if (report.EffectiveOverlayReadStatus == 0)
            {
                report.EffectiveOverlay = overlay;
            }
        }
        catch (EntryPointNotFoundException)
        {
            report.EffectiveOverlayReadStatus = null;
        }

        for (byte powerClass = 0; powerClass < FrequencyLimitSettings.Length; powerClass++)
        {
            report.ProcessorFrequencyLimits.Add(ReadSetting(
                report.ActiveScheme.Value,
                powerClass,
                FrequencyLimitSettings[powerClass]));
        }

        report.Status = "Completed";
        return report;
    }

    internal static FrequencyDomainTestReport RunFrequencyDomainTest(
        PowerPlanReport powerPlan,
        IReadOnlyList<LogicalProcessorReport> logicalProcessors,
        uint requestedLimitMhz)
    {
        var result = new FrequencyDomainTestReport
        {
            RequestedLimitMhz = requestedLimitMhz,
            LoadProbeDurationMilliseconds = LoadProbeDurationMilliseconds
        };

        if (!powerPlan.ActiveScheme.HasValue)
        {
            result.Status = "Refused";
            result.Error = "The active power scheme could not be read.";
            return result;
        }

        if (IsHelperRunning())
        {
            result.Status = "Refused";
            result.Error = "XboxGamingBarHelper is running and could overwrite the temporary test values. Stop it before testing.";
            return result;
        }

        Guid scheme = powerPlan.ActiveScheme.Value;
        result.Status = "Running";
        foreach (ProcessorFrequencyLimitSetting setting in powerPlan.ProcessorFrequencyLimits.Where(item => item.Present))
        {
            FrequencyDomainTestStep step = RunStep(scheme, setting, logicalProcessors, requestedLimitMhz);
            result.Steps.Add(step);
            if (!step.RestoreSucceeded || !step.RestoreReadbackSucceeded)
            {
                result.Status = "RestoreFailed";
                result.Error = $"Power class {setting.PowerEfficiencyClass} did not restore cleanly. No further writes were attempted.";
                return result;
            }

            if (!string.IsNullOrEmpty(step.Error))
            {
                result.Status = "StepFailed";
                result.Error = $"Power class {setting.PowerEfficiencyClass}: {step.Error}";
                return result;
            }
        }

        result.Status = result.Steps.Count == 0 ? "NoSettingsPresent" : "Completed";
        return result;
    }

    private static FrequencyDomainTestStep RunStep(
        Guid scheme,
        ProcessorFrequencyLimitSetting setting,
        IReadOnlyList<LogicalProcessorReport> logicalProcessors,
        uint requestedLimitMhz)
    {
        var step = new FrequencyDomainTestStep
        {
            PowerEfficiencyClass = setting.PowerEfficiencyClass,
            SettingGuid = setting.SettingGuid,
            RequestedLimitMhz = requestedLimitMhz,
            OriginalAcValueMhz = setting.AcValueMhz!.Value,
            OriginalDcValueMhz = setting.DcValueMhz!.Value,
            AcWriteStatus = uint.MaxValue,
            DcWriteStatus = uint.MaxValue,
            ApplySchemeStatus = uint.MaxValue,
            AppliedAcReadStatus = uint.MaxValue,
            AppliedDcReadStatus = uint.MaxValue,
            RestoreAcStatus = uint.MaxValue,
            RestoreDcStatus = uint.MaxValue,
            RestoreSchemeStatus = uint.MaxValue,
            RestoredAcReadStatus = uint.MaxValue,
            RestoredDcReadStatus = uint.MaxValue
        };

        IReadOnlyDictionary<ProcessorKey, ulong> beforeThroughput = ProcessorLoadProbe.Measure(
            logicalProcessors,
            LoadProbeDurationMilliseconds);
        List<ProcessorPowerSample> before = NativeTopologyReader.ReadProcessorPowerInformation(logicalProcessors.Count);
        Guid subgroup = ProcessorSettingsSubgroup;
        Guid settingGuid = setting.SettingGuid;
        try
        {
            step.AcWriteStatus = NativeMethods.PowerWriteACValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref settingGuid,
                requestedLimitMhz);
            settingGuid = setting.SettingGuid;
            step.DcWriteStatus = NativeMethods.PowerWriteDCValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref settingGuid,
                requestedLimitMhz);
            if (step.AcWriteStatus != 0 || step.DcWriteStatus != 0)
            {
                return step;
            }

            step.ApplySchemeStatus = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (step.ApplySchemeStatus != 0)
            {
                return step;
            }

            ProcessorFrequencyLimitSetting appliedSetting = ReadSetting(
                scheme,
                setting.PowerEfficiencyClass,
                setting.SettingGuid);
            step.AppliedAcReadStatus = appliedSetting.AcReadStatus;
            step.AppliedDcReadStatus = appliedSetting.DcReadStatus;
            step.AppliedAcValueMhz = appliedSetting.AcValueMhz;
            step.AppliedDcValueMhz = appliedSetting.DcValueMhz;
            if (!step.AppliedLimitReadbackSucceeded)
            {
                step.Error = "The temporary AC/DC frequency limit did not read back as requested.";
                return step;
            }

            if (IsHelperRunning())
            {
                step.Error = "XboxGamingBarHelper restarted during the frequency-domain test.";
                return step;
            }

            Thread.Sleep(250);
            IReadOnlyDictionary<ProcessorKey, ulong> duringThroughput = ProcessorLoadProbe.Measure(
                logicalProcessors,
                LoadProbeDurationMilliseconds);
            if (IsHelperRunning())
            {
                step.Error = "XboxGamingBarHelper restarted during the pinned load measurement.";
                return step;
            }

            List<ProcessorPowerSample> during = NativeTopologyReader.ReadProcessorPowerInformation(logicalProcessors.Count);
            int count = Math.Min(logicalProcessors.Count, Math.Min(before.Count, during.Count));
            for (int index = 0; index < count; index++)
            {
                LogicalProcessorReport processor = logicalProcessors[index];
                var processorKey = new ProcessorKey(processor.Group, processor.LogicalProcessorIndex);
                step.Observations.Add(new FrequencyDomainObservation
                {
                    ProcessorNumber = processor.ProcessorNumber,
                    Group = processor.Group,
                    LogicalProcessorIndex = processor.LogicalProcessorIndex,
                    RawEfficiencyClass = processor.EfficiencyClass,
                    Classification = processor.Classification,
                    BeforeMhzLimit = before[index].MhzLimit,
                    DuringMhzLimit = during[index].MhzLimit,
                    BeforeOperationsPerSecond = beforeThroughput.GetValueOrDefault(processorKey),
                    DuringOperationsPerSecond = duringThroughput.GetValueOrDefault(processorKey)
                });
            }
        }
        catch (Exception exception)
        {
            step.Error = exception.Message;
        }
        finally
        {
            settingGuid = setting.SettingGuid;
            step.RestoreAcStatus = NativeMethods.PowerWriteACValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref settingGuid,
                step.OriginalAcValueMhz);
            settingGuid = setting.SettingGuid;
            step.RestoreDcStatus = NativeMethods.PowerWriteDCValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref settingGuid,
                step.OriginalDcValueMhz);
            step.RestoreSchemeStatus = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            Thread.Sleep(250);
            ProcessorFrequencyLimitSetting restoredSetting = ReadSetting(
                scheme,
                setting.PowerEfficiencyClass,
                setting.SettingGuid);
            step.RestoredAcReadStatus = restoredSetting.AcReadStatus;
            step.RestoredDcReadStatus = restoredSetting.DcReadStatus;
            step.RestoredAcValueMhz = restoredSetting.AcValueMhz;
            step.RestoredDcValueMhz = restoredSetting.DcValueMhz;
        }

        return step;
    }

    private static ProcessorFrequencyLimitSetting ReadSetting(Guid scheme, byte powerClass, Guid settingGuid)
    {
        Guid subgroup = ProcessorSettingsSubgroup;
        Guid acSetting = settingGuid;
        uint acStatus = NativeMethods.PowerReadACValueIndex(
            IntPtr.Zero,
            ref scheme,
            ref subgroup,
            ref acSetting,
            out uint acValue);

        Guid dcSetting = settingGuid;
        uint dcStatus = NativeMethods.PowerReadDCValueIndex(
            IntPtr.Zero,
            ref scheme,
            ref subgroup,
            ref dcSetting,
            out uint dcValue);

        return new ProcessorFrequencyLimitSetting
        {
            PowerEfficiencyClass = powerClass,
            SettingGuid = settingGuid,
            AcReadStatus = acStatus,
            DcReadStatus = dcStatus,
            AcValueMhz = acStatus == 0 ? acValue : null,
            DcValueMhz = dcStatus == 0 ? dcValue : null
        };
    }

    private static bool IsHelperRunning()
    {
        Process[] helperProcesses = Process.GetProcessesByName("XboxGamingBarHelper");
        try
        {
            return helperProcesses.Length > 0;
        }
        finally
        {
            foreach (Process process in helperProcesses)
            {
                process.Dispose();
            }
        }
    }
}
