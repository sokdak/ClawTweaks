# CPU Topology Probe

This Windows-only diagnostic tool collects the evidence needed to distinguish P, E, and LP-E cores before changing the XboxGamingBarHelper control protocol.

## Run

```powershell
dotnet run --project Tools/CpuTopologyProbe/CpuTopologyProbe.csproj -c Release -- --output cpu-topology.json
```

The project targets 64-bit .NET 8 because each logical processor is pinned with `SetThreadGroupAffinity` while CPUID is executed.

## Evidence

- `GetSystemCpuSetInformation`: processor group/index, core, LLC, NUMA, efficiency and scheduling classes, and CPU-set flags.
- `GetLogicalProcessorInformationEx(RelationProcessorCore)`: physical-core masks and SMT cross-checking.
- `CallNtPowerInformation(ProcessorInformation)`: per-logical-processor maximum, current, and limited MHz.
- CPUID leaf `0x1A`: Intel Core (`0x40`) versus Atom (`0x20`) core type.
- CPUID leaf `0x1F`, falling back to `0x0B`: SMT, core, module, die, package, and x2APIC topology.
- Active power-plan readback for processor frequency-limit classes 0, 1, and 2 (`...e100`, `...e101`, `...e102`).
- SMBIOS registry identity fields needed to prove that a report came from a Claw 8 EX (`MS-1T91` / `CG3EM`).

Atom-class cores are split only when Windows reports at least two distinct `SchedulingClass` values. The highest value is reported as E, the lowest as LP-E, and ambiguous values remain `EfficientUnknown`. Logical processor numbering is never used as classification evidence.

`helperPowerClassMappings` reproduces the helper's current raw-`EfficiencyClass` ranking. A read-only report can reject unsupported layouts, but it leaves `supportsIndependentLpeFrequency` as `null` when all prerequisites pass because register presence alone does not prove isolation.

## Frequency-domain test

For final on-device verification, stop `XboxGamingBarHelper`, select Windows Balanced power mode, and run:

```powershell
dotnet run --project Tools/CpuTopologyProbe/CpuTopologyProbe.csproj -c Release -- --test-frequency-domains --output claw8-ex-cpu-topology.json
```

This optional test temporarily writes `1200 MHz` to each frequency-limit class, samples every logical processor's `MhzLimit`, then restores the original AC/DC value in a `finally` block before moving to the next class. It refuses to start while `XboxGamingBarHelper` is running. Do not interrupt or power off the machine during the test.

`IA32_HWP_CAPABILITIES` (`0x771`) remains `NotProbed`; reading an MSR requires a trusted kernel driver.
