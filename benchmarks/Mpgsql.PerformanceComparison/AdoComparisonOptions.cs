using System.Globalization;

// Only the ADO acceptance modes use these settings. Calibration is independent
// of the final samples and fixes both sample duration and count before inference.
internal sealed record AdoComparisonOptions
{
    internal const double CriticalValue = 2.023; // conservative t(39), for at least 40 final pairs
    public int PairCount { get; init; } = 40;
    public int MaximumPairCount { get; init; } = 40;
    public int WarmupSeconds { get; init; } = 5;
    public int PilotPairCount { get; init; } = 6;
    public int TargetBlockMilliseconds { get; init; } = 500;
    public int MaximumBlockMilliseconds { get; init; } = 2000;
    public int MaximumInvocations { get; init; } = 65536;
    public double TargetHalfWidthPercent { get; init; } = 0.25;
    public bool AdaptivePilot { get; init; } = true;
    public bool CoalesceReplies { get; init; }

    internal static AdoComparisonOptions FromEnvironment()
    {
        var pairs = Integer("MPGSQL_ADO_PAIRS", 40);
        var options = new AdoComparisonOptions
        {
            PairCount = pairs,
            MaximumPairCount = Integer("MPGSQL_ADO_MAX_PAIRS", pairs),
            WarmupSeconds = Integer("MPGSQL_ADO_WARMUP_SECONDS", 5),
            PilotPairCount = Integer("MPGSQL_ADO_PILOT_PAIRS", 6),
            TargetBlockMilliseconds = Integer("MPGSQL_ADO_BLOCK_MS", 500),
            MaximumBlockMilliseconds = Integer("MPGSQL_ADO_MAX_BLOCK_MS", 2000),
            MaximumInvocations = Integer("MPGSQL_ADO_MAX_INVOCATIONS", 65536),
            TargetHalfWidthPercent = Number("MPGSQL_ADO_HALF_WIDTH_PERCENT", 0.25),
            AdaptivePilot = Boolean("MPGSQL_ADO_ADAPTIVE", true),
            CoalesceReplies = Boolean("MPGSQL_ADO_COALESCE_REPLIES", false)
        };
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (PairCount is < 40 or > 400 || (PairCount & 1) != 0
            || MaximumPairCount < PairCount || MaximumPairCount > 400 || (MaximumPairCount & 1) != 0)
            throw new ArgumentException("ADO final pair counts must be even, from 40 to 400, with max >= base.");
        if (PilotPairCount is < 4 or > 20 || (PilotPairCount & 1) != 0)
            throw new ArgumentException("ADO pilot pairs must be even, from 4 to 20.");
        if (WarmupSeconds is < 1 or > 30 || TargetBlockMilliseconds is < 120 or > 5000
            || MaximumBlockMilliseconds < TargetBlockMilliseconds || MaximumBlockMilliseconds > 10000
            || MaximumInvocations is < 4096 or > 1000000)
            throw new ArgumentException("Invalid ADO warmup, block duration or invocation limit.");
        if (!double.IsFinite(TargetHalfWidthPercent) || TargetHalfWidthPercent is < 0.05 or > 2)
            throw new ArgumentException("ADO target CI half-width must be from 0.05% to 2%.");
    }

    private static int Integer(string name, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return fallback;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("Invalid integer setting " + name + ".");
    }
    private static double Number(string name, double fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return fallback;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("Invalid numeric setting " + name + ".");
    }
    private static bool Boolean(string name, bool fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return fallback;
        return bool.TryParse(text, out var value)
            ? value : throw new ArgumentException("Invalid boolean setting " + name + ".");
    }
}
