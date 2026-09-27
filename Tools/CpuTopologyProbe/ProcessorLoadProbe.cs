using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CpuTopologyProbe;

internal static class ProcessorLoadProbe
{
    private const int OperationsPerBlock = 4096;

    internal static IReadOnlyDictionary<ProcessorKey, ulong> Measure(
        IReadOnlyList<LogicalProcessorReport> logicalProcessors,
        int durationMilliseconds)
    {
        if (logicalProcessors.Count == 0)
        {
            return new Dictionary<ProcessorKey, ulong>();
        }

        if (durationMilliseconds < 250)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        _ = RunBlock(0x9e3779b97f4a7c15UL);
        var results = new ulong[logicalProcessors.Count];
        var errors = new Exception?[logicalProcessors.Count];
        var ready = new CountdownEvent(logicalProcessors.Count);
        var start = new ManualResetEventSlim(false);
        var done = new CountdownEvent(logicalProcessors.Count);

        for (int index = 0; index < logicalProcessors.Count; index++)
        {
            int workerIndex = index;
            LogicalProcessorReport processor = logicalProcessors[workerIndex];
            var thread = new Thread(() => MeasureWorker(
                workerIndex,
                processor,
                durationMilliseconds,
                ready,
                start,
                done,
                results,
                errors))
            {
                IsBackground = true,
                Name = $"CpuTopologyProbe-{processor.Group}-{processor.LogicalProcessorIndex}"
            };
            thread.Start();
        }

        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            start.Set();
            done.Wait(TimeSpan.FromMilliseconds(durationMilliseconds + 10_000));
            throw new TimeoutException("Timed out while pinning load-probe workers to logical processors.");
        }

        start.Set();
        if (!done.Wait(TimeSpan.FromMilliseconds(durationMilliseconds + 10_000)))
        {
            throw new TimeoutException("Timed out while waiting for the pinned load probe to finish.");
        }

        ready.Dispose();
        start.Dispose();
        done.Dispose();

        Exception[] failures = errors.Where(error => error is not null).Cast<Exception>().ToArray();
        if (failures.Length > 0)
        {
            throw new AggregateException("One or more pinned load-probe workers failed.", failures);
        }

        var measurements = new Dictionary<ProcessorKey, ulong>();
        for (int index = 0; index < logicalProcessors.Count; index++)
        {
            LogicalProcessorReport processor = logicalProcessors[index];
            measurements.Add(
                new ProcessorKey(processor.Group, processor.LogicalProcessorIndex),
                results[index]);
        }

        return measurements;
    }

    private static void MeasureWorker(
        int workerIndex,
        LogicalProcessorReport processor,
        int durationMilliseconds,
        CountdownEvent ready,
        ManualResetEventSlim start,
        CountdownEvent done,
        ulong[] results,
        Exception?[] errors)
    {
        bool readySignaled = false;
        bool affinitySet = false;
        NativeMethods.GroupAffinity previousAffinity = default;
        try
        {
            if (processor.LogicalProcessorIndex >= 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processor.LogicalProcessorIndex),
                    "Logical processor indexes above 63 are not valid within a processor group.");
            }

            var targetAffinity = new NativeMethods.GroupAffinity
            {
                Group = processor.Group,
                Mask = new UIntPtr(1UL << processor.LogicalProcessorIndex)
            };
            IntPtr thread = NativeMethods.GetCurrentThread();
            if (!NativeMethods.SetThreadGroupAffinity(thread, ref targetAffinity, out previousAffinity))
            {
                throw NativeMethods.LastError(nameof(NativeMethods.SetThreadGroupAffinity));
            }

            affinitySet = true;
            ready.Signal();
            readySignaled = true;
            start.Wait();

            long startTimestamp = Stopwatch.GetTimestamp();
            long durationTicks = checked((long)(durationMilliseconds * (double)Stopwatch.Frequency / 1000d));
            long deadline = startTimestamp + durationTicks;
            ulong state = 0x9e3779b97f4a7c15UL ^ checked((ulong)(workerIndex + 1));
            ulong operations = 0;
            long endTimestamp;
            do
            {
                state = RunBlock(state);
                operations += OperationsPerBlock;
                endTimestamp = Stopwatch.GetTimestamp();
            }
            while (endTimestamp < deadline);

            long elapsedTicks = Math.Max(1, endTimestamp - startTimestamp);
            results[workerIndex] = checked((ulong)Math.Round(
                operations * (double)Stopwatch.Frequency / elapsedTicks));
            GC.KeepAlive(state);
        }
        catch (Exception exception)
        {
            errors[workerIndex] = exception;
        }
        finally
        {
            if (!readySignaled)
            {
                ready.Signal();
            }

            if (affinitySet)
            {
                IntPtr thread = NativeMethods.GetCurrentThread();
                if (!NativeMethods.SetThreadGroupAffinity(thread, ref previousAffinity, out _)
                    && errors[workerIndex] is null)
                {
                    errors[workerIndex] = NativeMethods.LastError("restoring load-probe thread affinity");
                }
            }

            done.Signal();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RunBlock(ulong state)
    {
        for (int iteration = 0; iteration < OperationsPerBlock; iteration++)
        {
            state = unchecked(state * 2862933555777941757UL + 3037000493UL);
            state ^= state >> 29;
        }

        return state;
    }
}
