using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CpuTopologyProbe;

internal static class CpuTopologyCollector
{
    internal static CpuTopologyReport Collect(bool runFrequencyDomainTest = false, uint testLimitMhz = 1200)
    {
        var report = new CpuTopologyReport
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            SystemManufacturer = ReadBiosValue("SystemManufacturer"),
            SystemProductName = ReadBiosValue("SystemProductName"),
            BaseBoardProduct = ReadBiosValue("BaseBoardProduct"),
            BiosVersion = ReadBiosValue("BIOSVersion"),
            ProcessorName = ReadProcessorName()
        };

        List<CpuSetRecord> cpuSets = TryRead(
            report,
            "GetSystemCpuSetInformation",
            NativeTopologyReader.ReadCpuSets,
            value => report.Sources.SystemCpuSetInformation = value.Count > 0);
        List<ProcessorCoreRecord> coreRecords = TryRead(
            report,
            "GetLogicalProcessorInformationEx",
            NativeTopologyReader.ReadProcessorCores,
            value => report.Sources.LogicalProcessorInformationEx = value.Count > 0);
        List<ProcessorKey> activeProcessors = TryRead(
            report,
            "GetActiveProcessorCount",
            NativeTopologyReader.EnumerateActiveProcessors,
            _ => { });

        var processorKeys = activeProcessors
            .Concat(cpuSets.Select(cpuSet => new ProcessorKey(cpuSet.Group, cpuSet.LogicalProcessorIndex)))
            .Concat(EnumerateCoreRecordProcessors(coreRecords))
            .Distinct()
            .OrderBy(key => key.Group)
            .ThenBy(key => key.LogicalProcessorIndex)
            .ToList();

        Dictionary<ProcessorKey, CpuSetRecord> cpuSetByProcessor = cpuSets
            .GroupBy(cpuSet => new ProcessorKey(cpuSet.Group, cpuSet.LogicalProcessorIndex))
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<ProcessorKey, ProcessorCoreRecord> coreByProcessor = BuildCoreLookup(coreRecords);

        for (int processorNumber = 0; processorNumber < processorKeys.Count; processorNumber++)
        {
            ProcessorKey key = processorKeys[processorNumber];
            cpuSetByProcessor.TryGetValue(key, out CpuSetRecord? cpuSet);
            coreByProcessor.TryGetValue(key, out ProcessorCoreRecord? core);
            report.LogicalProcessors.Add(CreateLogicalProcessor(processorNumber, key, cpuSet, core));
        }

        ReadPowerInformation(report);
        ReadCpuId(report);
        ReadPowerPlan(report);
        ClassifyLogicalProcessors(report);
        BuildPhysicalCoreReports(report, coreRecords);
        BuildHelperPowerClassMappings(report);
        if (runFrequencyDomainTest)
        {
            report.FrequencyDomainTest = PowerPlanReader.RunFrequencyDomainTest(
                report.PowerPlan,
                report.LogicalProcessors,
                testLimitMhz);
        }

        SetCapabilities(report);
        AddCrossValidationIssues(report);

        return report;
    }

    internal static void Reassess(CpuTopologyReport report)
    {
        report.SchemaVersion = 4;
        if (report.HelperPowerClassMappings.Count == 0)
        {
            BuildHelperPowerClassMappings(report);
        }

        SetCapabilities(report);
    }

    private static T TryRead<T>(
        CpuTopologyReport report,
        string source,
        Func<T> reader,
        Action<T> onSuccess)
        where T : new()
    {
        try
        {
            T value = reader();
            onSuccess(value);
            return value;
        }
        catch (Exception exception)
        {
            report.Issues.Add(CreateIssue(source, "Error", exception));
            return new T();
        }
    }

    private static IEnumerable<ProcessorKey> EnumerateCoreRecordProcessors(IEnumerable<ProcessorCoreRecord> coreRecords)
    {
        foreach (ProcessorCoreRecord core in coreRecords)
        {
            foreach (NativeGroupMask groupMask in core.GroupMasks)
            {
                for (byte logicalProcessorIndex = 0; logicalProcessorIndex < 64; logicalProcessorIndex++)
                {
                    if ((groupMask.Mask & (1UL << logicalProcessorIndex)) != 0)
                    {
                        yield return new ProcessorKey(groupMask.Group, logicalProcessorIndex);
                    }
                }
            }
        }
    }

    private static Dictionary<ProcessorKey, ProcessorCoreRecord> BuildCoreLookup(IEnumerable<ProcessorCoreRecord> coreRecords)
    {
        var result = new Dictionary<ProcessorKey, ProcessorCoreRecord>();
        foreach (ProcessorCoreRecord core in coreRecords)
        {
            foreach (ProcessorKey key in EnumerateCoreRecordProcessors(new[] { core }))
            {
                result.TryAdd(key, core);
            }
        }

        return result;
    }

    private static LogicalProcessorReport CreateLogicalProcessor(
        int processorNumber,
        ProcessorKey key,
        CpuSetRecord? cpuSet,
        ProcessorCoreRecord? core)
    {
        return new LogicalProcessorReport
        {
            ProcessorNumber = processorNumber,
            Group = key.Group,
            LogicalProcessorIndex = key.LogicalProcessorIndex,
            CpuSetId = cpuSet?.Id,
            CoreIndex = cpuSet?.CoreIndex,
            LastLevelCacheIndex = cpuSet?.LastLevelCacheIndex,
            NumaNodeIndex = cpuSet?.NumaNodeIndex,
            EfficiencyClass = cpuSet?.EfficiencyClass,
            SchedulingClass = cpuSet?.SchedulingClass,
            IsParked = cpuSet is null ? null : (cpuSet.Flags & 0x01) != 0,
            IsAllocated = cpuSet is null ? null : (cpuSet.Flags & 0x02) != 0,
            IsAllocatedToTargetProcess = cpuSet is null ? null : (cpuSet.Flags & 0x04) != 0,
            IsRealTime = cpuSet is null ? null : (cpuSet.Flags & 0x08) != 0,
            PhysicalCoreNumber = core?.PhysicalCoreNumber,
            CoreRelationshipEfficiencyClass = core?.EfficiencyClass,
            HasSmtFlag = core is null ? null : (core.Flags & 0x01) != 0
        };
    }

    private static void ReadPowerInformation(CpuTopologyReport report)
    {
        if (report.LogicalProcessors.Count == 0)
        {
            return;
        }

        try
        {
            List<ProcessorPowerSample> samples = NativeTopologyReader.ReadProcessorPowerInformation(report.LogicalProcessors.Count);
            int assignableCount = Math.Min(samples.Count, report.LogicalProcessors.Count);
            for (int index = 0; index < assignableCount; index++)
            {
                report.LogicalProcessors[index].Power = samples[index];
            }

            report.Sources.ProcessorPowerInformation = samples.Count > 0;
            if (samples.Count != report.LogicalProcessors.Count)
            {
                report.Issues.Add(new DiagnosticIssue
                {
                    Source = "CallNtPowerInformation",
                    Severity = "Warning",
                    Message = $"Returned {samples.Count} records for {report.LogicalProcessors.Count} logical processors."
                });
            }
        }
        catch (Exception exception)
        {
            report.Issues.Add(CreateIssue("CallNtPowerInformation", "Error", exception));
        }
    }

    private static void ReadCpuId(CpuTopologyReport report)
    {
        if (!CpuIdReader.IsSupported)
        {
            report.Issues.Add(new DiagnosticIssue
            {
                Source = "CPUID",
                Severity = "Warning",
                Message = "CPUID is unavailable because the probe is not running as a 64-bit x86 process."
            });
            return;
        }

        int successCount = 0;
        foreach (LogicalProcessorReport processor in report.LogicalProcessors)
        {
            try
            {
                processor.CpuId = CpuIdReader.Read(processor.Group, processor.LogicalProcessorIndex);
                successCount++;
            }
            catch (Exception exception)
            {
                report.Issues.Add(CreateIssue(
                    $"CPUID group {processor.Group}, logical processor {processor.LogicalProcessorIndex}",
                    "Error",
                    exception));
            }
        }

        report.Sources.CpuId = successCount > 0;
    }

    private static void ReadPowerPlan(CpuTopologyReport report)
    {
        try
        {
            report.PowerPlan = PowerPlanReader.Read();
            report.Sources.PowerPlanSettings = report.PowerPlan.Status == "Completed";
        }
        catch (Exception exception)
        {
            report.PowerPlan.Status = "Failed";
            report.Issues.Add(CreateIssue("PowerPlanSettings", "Error", exception));
        }
    }

    private static void ClassifyLogicalProcessors(CpuTopologyReport report)
    {
        byte[] atomSchedulingClasses = report.LogicalProcessors
            .Where(processor => processor.CpuId?.HybridCoreTypeCode == "0x20" && processor.SchedulingClass.HasValue)
            .Select(processor => processor.SchedulingClass!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        byte? lowestAtomSchedulingClass = atomSchedulingClasses.Length > 1 ? atomSchedulingClasses[0] : null;
        byte? highestAtomSchedulingClass = atomSchedulingClasses.Length > 1 ? atomSchedulingClasses[^1] : null;

        foreach (LogicalProcessorReport processor in report.LogicalProcessors)
        {
            string? coreType = processor.CpuId?.HybridCoreTypeCode;
            if (coreType == "0x40")
            {
                processor.Classification = CpuCoreKind.Performance;
                processor.Confidence = ClassificationConfidence.High;
                processor.Evidence.Add("CPUID leaf 0x1A reports Intel Core type 0x40.");
                continue;
            }

            if (coreType != "0x20")
            {
                processor.Classification = CpuCoreKind.Unknown;
                processor.Confidence = ClassificationConfidence.None;
                processor.Evidence.Add(coreType is null
                    ? "CPUID leaf 0x1A did not provide a hybrid core type."
                    : $"CPUID leaf 0x1A reports unrecognized core type {coreType}.");
                continue;
            }

            processor.Evidence.Add("CPUID leaf 0x1A reports Intel Atom type 0x20.");
            if (!processor.SchedulingClass.HasValue || !lowestAtomSchedulingClass.HasValue || !highestAtomSchedulingClass.HasValue)
            {
                processor.Classification = CpuCoreKind.EfficientUnknown;
                processor.Confidence = ClassificationConfidence.Low;
                processor.Evidence.Add("SchedulingClass does not distinguish E from LP-E cores.");
                continue;
            }

            if (processor.SchedulingClass.Value == highestAtomSchedulingClass.Value)
            {
                processor.Classification = CpuCoreKind.Efficiency;
                processor.Confidence = ClassificationConfidence.Medium;
                processor.Evidence.Add($"SchedulingClass {processor.SchedulingClass.Value} is the highest Atom-class value.");
            }
            else if (processor.SchedulingClass.Value == lowestAtomSchedulingClass.Value)
            {
                processor.Classification = CpuCoreKind.LowPowerEfficiency;
                processor.Confidence = ClassificationConfidence.Medium;
                processor.Evidence.Add($"SchedulingClass {processor.SchedulingClass.Value} is the lowest Atom-class value.");
            }
            else
            {
                processor.Classification = CpuCoreKind.EfficientUnknown;
                processor.Confidence = ClassificationConfidence.Low;
                processor.Evidence.Add($"SchedulingClass {processor.SchedulingClass.Value} is between the highest and lowest Atom-class values.");
            }
        }
    }

    private static void BuildPhysicalCoreReports(CpuTopologyReport report, IReadOnlyCollection<ProcessorCoreRecord> coreRecords)
    {
        if (coreRecords.Count > 0)
        {
            foreach (ProcessorCoreRecord core in coreRecords)
            {
                List<LogicalProcessorReport> members = report.LogicalProcessors
                    .Where(processor => processor.PhysicalCoreNumber == core.PhysicalCoreNumber)
                    .ToList();
                report.PhysicalCores.Add(CreatePhysicalCore(core, members));
            }

            return;
        }

        var syntheticGroups = report.LogicalProcessors
            .Where(processor => processor.CoreIndex.HasValue)
            .GroupBy(processor => new { processor.Group, processor.CoreIndex })
            .OrderBy(group => group.Key.Group)
            .ThenBy(group => group.Key.CoreIndex);
        int physicalCoreNumber = 0;
        foreach (var group in syntheticGroups)
        {
            List<LogicalProcessorReport> members = group.ToList();
            var syntheticCore = new ProcessorCoreRecord
            {
                PhysicalCoreNumber = physicalCoreNumber++,
                EfficiencyClass = members[0].EfficiencyClass ?? 0,
                Flags = checked((byte)(members.Count > 1 ? 1 : 0))
            };
            ulong mask = members.Aggregate(0UL, (current, member) => current | (1UL << member.LogicalProcessorIndex));
            syntheticCore.GroupMasks.Add(new NativeGroupMask(group.Key.Group, mask));
            report.PhysicalCores.Add(CreatePhysicalCore(syntheticCore, members));
        }
    }

    private static PhysicalCoreReport CreatePhysicalCore(
        ProcessorCoreRecord core,
        IReadOnlyCollection<LogicalProcessorReport> members)
    {
        var result = new PhysicalCoreReport
        {
            PhysicalCoreNumber = core.PhysicalCoreNumber,
            EfficiencyClass = core.EfficiencyClass,
            HasSmtFlag = (core.Flags & 0x01) != 0,
            GroupMasks = core.GroupMasks.Select(groupMask => new ProcessorGroupMask
            {
                Group = groupMask.Group,
                Mask = $"0x{groupMask.Mask:X16}"
            }).ToList(),
            LogicalProcessors = members.Select(member => new ProcessorAddress
            {
                Group = member.Group,
                LogicalProcessorIndex = member.LogicalProcessorIndex
            }).ToList()
        };

        CpuCoreKind[] classifications = members.Select(member => member.Classification).Distinct().ToArray();
        if (classifications.Length == 1)
        {
            result.Classification = classifications[0];
            result.Confidence = members.Min(member => member.Confidence);
            result.Evidence = members.SelectMany(member => member.Evidence).Distinct().ToList();
        }
        else
        {
            result.Classification = CpuCoreKind.Unknown;
            result.Confidence = ClassificationConfidence.Low;
            result.Evidence.Add("Logical processors belonging to this physical core have conflicting classifications.");
        }

        return result;
    }

    private static void BuildHelperPowerClassMappings(CpuTopologyReport report)
    {
        byte[] rawClasses = report.LogicalProcessors
            .Where(processor => processor.EfficiencyClass.HasValue)
            .Select(processor => processor.EfficiencyClass!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        for (int index = 0; index < rawClasses.Length; index++)
        {
            byte rawClass = rawClasses[index];
            byte powerClass = checked((byte)index);
            List<LogicalProcessorReport> logicalProcessors = report.LogicalProcessors
                .Where(processor => processor.EfficiencyClass == rawClass)
                .ToList();
            ProcessorFrequencyLimitSetting? setting = report.PowerPlan.ProcessorFrequencyLimits
                .FirstOrDefault(item => item.PowerEfficiencyClass == powerClass);
            report.HelperPowerClassMappings.Add(new PowerEfficiencyClassMapping
            {
                RawEfficiencyClass = rawClass,
                HelperPowerEfficiencyClass = powerClass,
                Classifications = logicalProcessors
                    .Select(processor => processor.Classification)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList(),
                LogicalProcessorCount = logicalProcessors.Count,
                PhysicalCoreCount = logicalProcessors
                    .Where(processor => processor.PhysicalCoreNumber.HasValue)
                    .Select(processor => processor.PhysicalCoreNumber!.Value)
                    .Distinct()
                    .Count(),
                FrequencyLimitSettingPresent = setting?.Present == true
            });
        }
    }

    private static void SetCapabilities(CpuTopologyReport report)
    {
        bool hasPerformance = report.PhysicalCores.Any(core => core.Classification == CpuCoreKind.Performance);
        bool hasEfficient = report.PhysicalCores.Any(core => core.Classification is
            CpuCoreKind.Efficiency or CpuCoreKind.LowPowerEfficiency or CpuCoreKind.EfficientUnknown);
        bool hasECore = report.PhysicalCores.Any(core => core.Classification == CpuCoreKind.Efficiency);
        bool hasLpeCore = report.PhysicalCores.Any(core => core.Classification == CpuCoreKind.LowPowerEfficiency);

        report.Capabilities.IsHybrid = hasPerformance && hasEfficient
            || report.LogicalProcessors.Any(processor => processor.CpuId?.HybridFeatureFlag == true);
        report.Capabilities.HasDistinctLpeTopology = hasECore && hasLpeCore;
        AssessIndependentLpeFrequency(report);
    }

    private static void AssessIndependentLpeFrequency(CpuTopologyReport report)
    {
        if (!report.Capabilities.HasDistinctLpeTopology)
        {
            report.Capabilities.SupportsIndependentLpeFrequency = false;
            report.Capabilities.IndependentLpeFrequencyEvidence =
                "E and LP-E cores were not distinguished with sufficient topology evidence.";
            return;
        }

        List<PowerEfficiencyClassMapping> eMappings = report.HelperPowerClassMappings
            .Where(mapping => mapping.Classifications.Contains(CpuCoreKind.Efficiency))
            .ToList();
        List<PowerEfficiencyClassMapping> lpeMappings = report.HelperPowerClassMappings
            .Where(mapping => mapping.Classifications.Contains(CpuCoreKind.LowPowerEfficiency))
            .ToList();
        byte[] sharedPowerClasses = eMappings
            .Select(mapping => mapping.HelperPowerEfficiencyClass)
            .Intersect(lpeMappings.Select(mapping => mapping.HelperPowerEfficiencyClass))
            .ToArray();
        ProcessorFrequencyLimitSetting[] liveSettings = report.PowerPlan.ProcessorFrequencyLimits
            .Where(setting => setting.Present)
            .ToArray();
        if (liveSettings.Length == 0)
        {
            report.Capabilities.SupportsIndependentLpeFrequency = false;
            report.Capabilities.IndependentLpeFrequencyEvidence =
                "The active power scheme does not expose any processor frequency-limit class registers.";
            return;
        }

        if (report.FrequencyDomainTest is null)
        {
            report.Capabilities.SupportsIndependentLpeFrequency = null;
            report.Capabilities.IndependentLpeFrequencyEvidence = sharedPowerClasses.Length > 0
                ? $"The current helper maps E and LP-E to shared power class {string.Join("/", sharedPowerClasses)}, but this does not prove that e101/e102 are unused. Run --test-frequency-domains to measure every live register."
                : "Read-only prerequisites pass. Run --test-frequency-domains with XboxGamingBarHelper stopped to prove that E and LP-E writes affect separate processor domains.";
            return;
        }

        if (report.FrequencyDomainTest.Status != "Completed")
        {
            report.Capabilities.SupportsIndependentLpeFrequency = null;
            report.Capabilities.IndependentLpeFrequencyEvidence =
                $"Frequency-domain test status is {report.FrequencyDomainTest.Status}: {report.FrequencyDomainTest.Error}";
            return;
        }

        bool? isolated = VerifyObservedLpeOverride(report, out string testEvidence);
        report.Capabilities.SupportsIndependentLpeFrequency = isolated;
        report.Capabilities.IndependentLpeFrequencyEvidence = testEvidence;
    }

    private static bool? VerifyObservedLpeOverride(CpuTopologyReport report, out string evidence)
    {
        FrequencyDomainTestStep[] completedSteps = report.FrequencyDomainTest!.Steps
            .Where(step => step.WriteSucceeded && step.RestoreSucceeded && step.Observations.Count > 0)
            .ToArray();
        if (completedSteps.Length == 0)
        {
            evidence = "No frequency-domain step completed with both measurements and a successful restore.";
            return null;
        }

        int eProcessorCount = report.LogicalProcessors.Count(processor => processor.Classification == CpuCoreKind.Efficiency);
        int lpeProcessorCount = report.LogicalProcessors.Count(processor => processor.Classification == CpuCoreKind.LowPowerEfficiency);
        FrequencyDomainTestStep? lpeOnlyStep = completedSteps.FirstOrDefault(step =>
        {
            FrequencyDomainObservation[] changed = step.Observations.Where(item => item.Changed).ToArray();
            return changed.Length == lpeProcessorCount
                && changed.All(item => item.Classification == CpuCoreKind.LowPowerEfficiency);
        });
        FrequencyDomainTestStep? eStep = completedSteps.FirstOrDefault(step =>
        {
            FrequencyDomainObservation[] changed = step.Observations.Where(item => item.Changed).ToArray();
            int changedECount = changed.Count(item => item.Classification == CpuCoreKind.Efficiency);
            return changedECount == eProcessorCount
                && changed.All(item => item.Classification is CpuCoreKind.Efficiency or CpuCoreKind.LowPowerEfficiency);
        });

        if (lpeOnlyStep is not null && eStep is not null
            && lpeOnlyStep.PowerEfficiencyClass != eStep.PowerEfficiencyClass)
        {
            string eScope = eStep.Observations.Any(item => item.Changed && item.Classification == CpuCoreKind.LowPowerEfficiency)
                ? "E+LP-E"
                : "E-only";
            evidence = $"Power class {lpeOnlyStep.PowerEfficiencyClass} reduced every LP-E processor's pinned-load throughput or MhzLimit without affecting P/E processors; power class {eStep.PowerEfficiencyClass} affected {eScope}. A dedicated LP-E override register is present.";
            return true;
        }

        bool sawEfficiencyChange = completedSteps.Any(step => step.Observations.Any(item =>
            item.Changed && item.Classification is CpuCoreKind.Efficiency or CpuCoreKind.LowPowerEfficiency));
        if (!sawEfficiencyChange)
        {
            evidence = "The test restored successfully, but no E or LP-E MhzLimit or pinned-load throughput changes were observed.";
            return null;
        }

        if (lpeOnlyStep is null)
        {
            string measured = string.Join("; ", completedSteps.Select(step =>
            {
                string kinds = string.Join("/", step.Observations
                    .Where(item => item.Changed)
                    .Select(item => item.Classification)
                    .Distinct());
                return $"class {step.PowerEfficiencyClass}={kinds}";
            }));
            evidence = "No register measurably affected every LP-E processor without also affecting P/E processors. Observed domains: " + measured + ".";
            return false;
        }

        evidence = $"Power class {lpeOnlyStep.PowerEfficiencyClass} appears LP-E-only, but no different register measurably affected every E processor. The E-side mapping remains inconclusive.";
        return null;
    }

    private static void AddCrossValidationIssues(CpuTopologyReport report)
    {
        foreach (LogicalProcessorReport processor in report.LogicalProcessors)
        {
            if (processor.EfficiencyClass.HasValue
                && processor.CoreRelationshipEfficiencyClass.HasValue
                && processor.EfficiencyClass.Value != processor.CoreRelationshipEfficiencyClass.Value)
            {
                report.Issues.Add(new DiagnosticIssue
                {
                    Source = "TopologyCrossCheck",
                    Severity = "Warning",
                    Message = $"Group {processor.Group}, logical processor {processor.LogicalProcessorIndex}: CPU-set EfficiencyClass {processor.EfficiencyClass.Value} differs from processor-core EfficiencyClass {processor.CoreRelationshipEfficiencyClass.Value}."
                });
            }
        }

        if (report.Capabilities.IsHybrid && !report.Capabilities.HasDistinctLpeTopology)
        {
            report.Issues.Add(new DiagnosticIssue
            {
                Source = "Classification",
                Severity = "Information",
                Message = "Hybrid topology was detected, but E and LP-E cores were not distinguished with sufficient evidence."
            });
        }

        if (report.HelperPowerClassMappings.Count > 3)
        {
            report.Issues.Add(new DiagnosticIssue
            {
                Source = "HelperPowerClassMapping",
                Severity = "Error",
                Message = $"Detected {report.HelperPowerClassMappings.Count} raw efficiency classes, but the helper only knows power frequency registers 0, 1, and 2."
            });
        }
    }

    private static DiagnosticIssue CreateIssue(string source, string severity, Exception exception)
    {
        int? nativeError = exception is System.ComponentModel.Win32Exception win32Exception
            ? win32Exception.NativeErrorCode
            : null;
        return new DiagnosticIssue
        {
            Source = source,
            Severity = severity,
            Message = exception.Message,
            NativeError = nativeError
        };
    }

    private static string ReadProcessorName()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                string.Empty)?.ToString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadBiosValue(string valueName)
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS",
                valueName,
                string.Empty);
            return value switch
            {
                string text => text.Trim(),
                string[] values => string.Join("; ", values.Where(item => !string.IsNullOrWhiteSpace(item))),
                _ => value?.ToString()?.Trim() ?? string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}
