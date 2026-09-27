using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuTopologyProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ProbeOptions options = ProbeOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine("Usage: CpuTopologyProbe [--output <path>] [--compact] [--test-frequency-domains] [--test-limit-mhz <mhz>] [--input <existing-report.json>]");
                return 0;
            }

            var serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = !options.Compact,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() }
            };
            CpuTopologyReport report;
            if (options.InputPath is null)
            {
                report = CpuTopologyCollector.Collect(
                    options.TestFrequencyDomains,
                    options.TestLimitMhz);
            }
            else
            {
                string inputPath = Path.GetFullPath(options.InputPath);
                report = JsonSerializer.Deserialize<CpuTopologyReport>(
                    File.ReadAllText(inputPath),
                    serializerOptions) ?? throw new InvalidDataException($"Could not deserialize {inputPath}.");
                CpuTopologyCollector.Reassess(report);
            }

            string json = JsonSerializer.Serialize(report, serializerOptions);

            if (options.OutputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                string outputPath = Path.GetFullPath(options.OutputPath);
                string? directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outputPath, json + Environment.NewLine);
                Console.Error.WriteLine($"CPU topology report written to {outputPath}");
            }

            return report.LogicalProcessors.Count > 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private sealed class ProbeOptions
    {
        internal string? OutputPath { get; private set; }
        internal string? InputPath { get; private set; }
        internal bool Compact { get; private set; }
        internal bool ShowHelp { get; private set; }
        internal bool TestFrequencyDomains { get; private set; }
        internal uint TestLimitMhz { get; private set; } = 2000;

        internal static ProbeOptions Parse(IReadOnlyList<string> args)
        {
            var options = new ProbeOptions();
            for (int index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--output":
                    case "-o":
                        if (++index >= args.Count)
                        {
                            throw new ArgumentException("--output requires a path.");
                        }

                        options.OutputPath = args[index];
                        break;
                    case "--compact":
                        options.Compact = true;
                        break;
                    case "--input":
                        if (++index >= args.Count)
                        {
                            throw new ArgumentException("--input requires a path.");
                        }

                        options.InputPath = args[index];
                        break;
                    case "--test-frequency-domains":
                        options.TestFrequencyDomains = true;
                        break;
                    case "--test-limit-mhz":
                        if (++index >= args.Count || !uint.TryParse(args[index], out uint limitMhz) || limitMhz < 400 || limitMhz > 3000)
                        {
                            throw new ArgumentException("--test-limit-mhz requires a value from 400 through 3000.");
                        }

                        options.TestLimitMhz = limitMhz;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {args[index]}");
                }
            }

            if (options.InputPath is not null && options.TestFrequencyDomains)
            {
                throw new ArgumentException("--input cannot be combined with --test-frequency-domains.");
            }

            return options;
        }
    }
}
