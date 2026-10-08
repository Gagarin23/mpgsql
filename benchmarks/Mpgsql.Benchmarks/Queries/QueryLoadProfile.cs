namespace Mpgsql.Benchmarks.Queries;

internal sealed record QueryLoadProfile(string Name, int Callers, int Connections, int InFlight, bool Mixed = false)
{
    internal long RowBudget => Mixed ? 65536 : 8 * 1024 * 1024;
    internal static readonly QueryLoadProfile[] All =
    [
        new("C1_P1_W1", 1, 1, 1), new("C8_P1_W8", 8, 1, 8),
        new("C64_P1_W8", 64, 1, 8), new("C64_P4_W8", 64, 4, 8),
        new("Mixed_C64_P1_W8", 64, 1, 8, true), new("Mixed_C64_P4_W8", 64, 4, 8, true)
    ];
    // Policy experiments are opt-in; keep the original baseline profiles unchanged.
    internal static readonly QueryLoadProfile[] WindowSweep =
        [.. from mixed in new[] { false, true }
            from connections in new[] { 1, 4 }
            from window in new[] { 8, 16, 32, 64 }
            select new QueryLoadProfile($"{(mixed ? "Mixed_" : "")}C64_P{connections}_W{window}",
                64, connections, window, mixed)];

    internal static QueryLoadProfile Find(string name) => All.FirstOrDefault(x => x.Name == name)
        ?? WindowSweep.Single(x => x.Name == name);

    internal QueryCatalog CreateCatalog() => new(Mixed ? [QueryScenario.One, QueryScenario.Slow] : [QueryScenario.One],
        Callers, mixed: Mixed);
}
