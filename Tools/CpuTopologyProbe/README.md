# CPU Topology Probe

This Windows-only diagnostic tool collects the evidence needed to distinguish P, E, and LP-E cores before changing the XboxGamingBarHelper control protocol.

## Run

Ready-to-run packages include `Collect-Claw8EXValidation.ps1` next to the self-contained executable. The recommended first pass is read-only:

```powershell
powershell -ExecutionPolicy Bypass -File .\Collect-Claw8EXValidation.ps1
```

It creates a timestamped folder and ZIP on the desktop. Share the ZIP for analysis.

Source-tree execution remains available when the .NET 8 SDK is installed:

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
powershell -ExecutionPolicy Bypass -File .\Collect-Claw8EXValidation.ps1 -FrequencyDomainTest
```

The script first runs a read-only preflight and refuses to write when E/LP-E topology is not distinguishable or no frequency register is present. A shared raw `EfficiencyClass` does not block the measurement: that is the exact Claw 8 EX case the active test must resolve. The optional test temporarily writes `1200 MHz` to each live frequency-limit class, samples every logical processor's `MhzLimit`, then restores the original AC/DC value in a `finally` block before moving to the next class. A pass requires one register that changes every LP-E processor without changing P/E processors, plus a different register that changes every E processor. It requires an explicit `EX-TEST` confirmation, refuses to start while `XboxGamingBarHelper` is running, checks Windows Balanced power mode, and verifies the post-test power-plan readback. Do not interrupt or power off the machine during the test.

`IA32_HWP_CAPABILITIES` (`0x771`) remains `NotProbed`; reading an MSR requires a trusted kernel driver.
