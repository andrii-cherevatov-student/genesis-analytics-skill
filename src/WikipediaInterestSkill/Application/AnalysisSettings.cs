using WikipediaInterestSkill.TimeSeries;

namespace WikipediaInterestSkill.Application;

public sealed record AnalysisSettings
{
    public const double ConfidenceLevel = 0.95;

    public const int MinimumReportingMonths = 12;

    public const int MinimumTotalMonths = 24;

    public const int DefaultBlockLength = 3;

    public double PracticalThreshold { get; init; } = 0.05;

    public int BootstrapIterations { get; init; } = 2000;

    public int BlockLength { get; init; } = DefaultBlockLength;

    public int HistoryMonths { get; init; } = 24;

    public double AnomalyThreshold { get; init; } = 3.0;

    public StlOptions Stl { get; init; } = new();

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    internal ParallelOptions Parallelism => new() { MaxDegreeOfParallelism = MaxDegreeOfParallelism };
}
