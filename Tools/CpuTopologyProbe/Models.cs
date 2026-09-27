using System.Text.Json.Serialization;

namespace CpuTopologyProbe;

internal enum CpuCoreKind
{
    Unknown,
    Performance,
    Efficiency,
    LowPowerEfficiency,
    EfficientUnknown
}

internal enum ClassificationConfidence
{
    None,
    Low,
    Medium,
    High
}

internal sealed class CpuTopologyReport
{
    public int SchemaVersion { get; set; } = 2;
    public DateTimeOffset CollectedAtUtc { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
    public string SystemManufacturer { get; set; } = string.Empty;
    public string SystemProductName { get; set; } = string.Empty;
    public string BaseBoardProduct { get; set; } = string.Empty;
    public string BiosVersion { get; set; } = string.Empty;
    public string ProcessorName { get; set; } = string.Empty;
    public ProbeSources Sources { get; set; } = new();
    public TopologyCapabilities Capabilities { get; set; } = new();
    public HwpCapabilityProbe HwpCapabilities { get; set; } = new();
    public PowerPlanReport PowerPlan { get; set; } = new();
    public List<PowerEfficiencyClassMapping> HelperPowerClassMappings { get; set; } = new();
    public FrequencyDomainTestReport? FrequencyDomainTest { get; set; }
    public List<LogicalProcessorReport> LogicalProcessors { get; set; } = new();
    public List<PhysicalCoreReport> PhysicalCores { get; set; } = new();
    public List<DiagnosticIssue> Issues { get; set; } = new();
}

internal sealed class ProbeSources
{
    public bool SystemCpuSetInformation { get; set; }
    public bool LogicalProcessorInformationEx { get; set; }
    public bool ProcessorPowerInformation { get; set; }
    public bool CpuId { get; set; }
    public bool PowerPlanSettings { get; set; }
}

internal sealed class TopologyCapabilities
{
    public bool IsHybrid { get; set; }
    public bool HasDistinctLpeTopology { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? SupportsIndependentLpeFrequency { get; set; }
    public string IndependentLpeFrequencyEvidence { get; set; } = string.Empty;
}

internal sealed class HwpCapabilityProbe
{
    public string Status { get; set; } = "NotProbed";
    public string Msr { get; set; } = "0x771";
    public string Reason { get; set; } = "Reading IA32_HWP_CAPABILITIES requires a trusted kernel driver.";
}

internal sealed class PowerPlanReport
{
    public string Status { get; set; } = "NotProbed";
    public Guid? ActiveScheme { get; set; }
    public Guid? EffectiveOverlay { get; set; }
    public uint? EffectiveOverlayReadStatus { get; set; }
    public List<ProcessorFrequencyLimitSetting> ProcessorFrequencyLimits { get; set; } = new();
}

internal sealed class ProcessorFrequencyLimitSetting
{
    public byte PowerEfficiencyClass { get; set; }
    public Guid SettingGuid { get; set; }
    public uint AcReadStatus { get; set; }
    public uint DcReadStatus { get; set; }
    public uint? AcValueMhz { get; set; }
    public uint? DcValueMhz { get; set; }
    public bool Present => AcReadStatus == 0 && DcReadStatus == 0;
}

internal sealed class PowerEfficiencyClassMapping
{
    public byte RawEfficiencyClass { get; set; }
    public byte HelperPowerEfficiencyClass { get; set; }
    public List<CpuCoreKind> Classifications { get; set; } = new();
    public int LogicalProcessorCount { get; set; }
    public int PhysicalCoreCount { get; set; }
    public bool FrequencyLimitSettingPresent { get; set; }
}

internal sealed class FrequencyDomainTestReport
{
    public string Status { get; set; } = "NotStarted";
    public uint RequestedLimitMhz { get; set; }
    public string? Error { get; set; }
    public List<FrequencyDomainTestStep> Steps { get; set; } = new();
}

internal sealed class FrequencyDomainTestStep
{
    public byte PowerEfficiencyClass { get; set; }
    public Guid SettingGuid { get; set; }
    public uint OriginalAcValueMhz { get; set; }
    public uint OriginalDcValueMhz { get; set; }
    public uint AcWriteStatus { get; set; }
    public uint DcWriteStatus { get; set; }
    public uint ApplySchemeStatus { get; set; }
    public uint RestoreAcStatus { get; set; }
    public uint RestoreDcStatus { get; set; }
    public uint RestoreSchemeStatus { get; set; }
    public string? Error { get; set; }
    public List<FrequencyDomainObservation> Observations { get; set; } = new();
    public bool WriteSucceeded => AcWriteStatus == 0 && DcWriteStatus == 0 && ApplySchemeStatus == 0;
    public bool RestoreSucceeded => RestoreAcStatus == 0 && RestoreDcStatus == 0 && RestoreSchemeStatus == 0;
}

internal sealed class FrequencyDomainObservation
{
    public int ProcessorNumber { get; set; }
    public ushort Group { get; set; }
    public byte LogicalProcessorIndex { get; set; }
    public byte? RawEfficiencyClass { get; set; }
    public CpuCoreKind Classification { get; set; }
    public uint BeforeMhzLimit { get; set; }
    public uint DuringMhzLimit { get; set; }
    public bool Changed => BeforeMhzLimit != DuringMhzLimit;
}

internal sealed class LogicalProcessorReport
{
    public int ProcessorNumber { get; set; }
    public ushort Group { get; set; }
    public byte LogicalProcessorIndex { get; set; }
    public uint? CpuSetId { get; set; }
    public byte? CoreIndex { get; set; }
    public byte? LastLevelCacheIndex { get; set; }
    public byte? NumaNodeIndex { get; set; }
    public byte? EfficiencyClass { get; set; }
    public byte? SchedulingClass { get; set; }
    public bool? IsParked { get; set; }
    public bool? IsAllocated { get; set; }
    public bool? IsAllocatedToTargetProcess { get; set; }
    public bool? IsRealTime { get; set; }
    public int? PhysicalCoreNumber { get; set; }
    public byte? CoreRelationshipEfficiencyClass { get; set; }
    public bool? HasSmtFlag { get; set; }
    public ProcessorPowerSample? Power { get; set; }
    public CpuIdSnapshot? CpuId { get; set; }
    public CpuCoreKind Classification { get; set; }
    public ClassificationConfidence Confidence { get; set; }
    public List<string> Evidence { get; set; } = new();
}

internal sealed class PhysicalCoreReport
{
    public int PhysicalCoreNumber { get; set; }
    public byte? EfficiencyClass { get; set; }
    public bool HasSmtFlag { get; set; }
    public List<ProcessorGroupMask> GroupMasks { get; set; } = new();
    public List<ProcessorAddress> LogicalProcessors { get; set; } = new();
    public CpuCoreKind Classification { get; set; }
    public ClassificationConfidence Confidence { get; set; }
    public List<string> Evidence { get; set; } = new();
}

internal sealed class ProcessorAddress
{
    public ushort Group { get; set; }
    public byte LogicalProcessorIndex { get; set; }
}

internal sealed class ProcessorGroupMask
{
    public ushort Group { get; set; }
    public string Mask { get; set; } = string.Empty;
}

internal sealed class ProcessorPowerSample
{
    public uint SourceProcessorNumber { get; set; }
    public uint MaxMhz { get; set; }
    public uint CurrentMhz { get; set; }
    public uint MhzLimit { get; set; }
    public uint MaxIdleState { get; set; }
    public uint CurrentIdleState { get; set; }
}

internal sealed class CpuIdSnapshot
{
    public uint MaxBasicLeaf { get; set; }
    public bool HybridFeatureFlag { get; set; }
    public string? HybridCoreTypeCode { get; set; }
    public string? HybridCoreTypeName { get; set; }
    public uint? NativeModelId { get; set; }
    public string? TopologyLeaf { get; set; }
    public uint? X2ApicId { get; set; }
    public uint? SmtId { get; set; }
    public uint? CoreId { get; set; }
    public uint? ModuleId { get; set; }
    public uint? DieId { get; set; }
    public uint? PackageId { get; set; }
    public List<CpuIdTopologyLevel> TopologyLevels { get; set; } = new();
}

internal sealed class CpuIdTopologyLevel
{
    public int SubLeaf { get; set; }
    public byte LevelNumber { get; set; }
    public byte LevelType { get; set; }
    public string LevelTypeName { get; set; } = string.Empty;
    public byte Shift { get; set; }
    public ushort LogicalProcessorsAtLevel { get; set; }
    public uint X2ApicId { get; set; }
}

internal sealed class DiagnosticIssue
{
    public string Source { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? NativeError { get; set; }
}

internal sealed class CpuSetRecord
{
    public uint Id { get; set; }
    public ushort Group { get; set; }
    public byte LogicalProcessorIndex { get; set; }
    public byte CoreIndex { get; set; }
    public byte LastLevelCacheIndex { get; set; }
    public byte NumaNodeIndex { get; set; }
    public byte EfficiencyClass { get; set; }
    public byte SchedulingClass { get; set; }
    public byte Flags { get; set; }
}

internal sealed class ProcessorCoreRecord
{
    public int PhysicalCoreNumber { get; set; }
    public byte Flags { get; set; }
    public byte EfficiencyClass { get; set; }
    public List<NativeGroupMask> GroupMasks { get; set; } = new();
}

internal readonly record struct NativeGroupMask(ushort Group, ulong Mask);

internal readonly record struct ProcessorKey(ushort Group, byte LogicalProcessorIndex);
