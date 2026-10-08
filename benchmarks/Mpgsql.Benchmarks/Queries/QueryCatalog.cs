namespace Mpgsql.Benchmarks.Queries;

internal sealed class QueryCatalog
{
    internal QueryScenario[] Scenarios { get; }
    internal byte[][][] Replies { get; }
    internal MpgsqlParameter[][][] Inputs { get; }
    internal long PreparedReplyBytes { get; }

    internal QueryCatalog(QueryScenario[] scenarios, int workers, bool mixed = false)
    {
        Scenarios = scenarios;
        Replies = new byte[scenarios.Length][][];
        Inputs = new MpgsqlParameter[scenarios.Length][][];
        for (int i = 0; i < scenarios.Length; i++)
        {
            Replies[i] = new byte[workers][];
            Inputs[i] = new MpgsqlParameter[workers][];
            for (int worker = 0; worker < workers; worker++)
            {
                Inputs[i][worker] = scenarios[i].Parameters(worker);
                if (mixed && scenarios[i] == QueryScenario.Slow && worker % 8 != 0)
                    Replies[i][worker] = [];
                else
                    Replies[i][worker] = QueryWire.Reply(scenarios[i], worker);
                PreparedReplyBytes += Replies[i][worker].Length;
            }
        }
    }

    internal int Index(QueryScenario scenario) => Array.IndexOf(Scenarios, scenario) is >= 0 and var index
        ? index : throw new ArgumentException("Scenario is not in the peer catalog.");
}
