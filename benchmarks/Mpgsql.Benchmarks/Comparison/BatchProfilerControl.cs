using System.Reflection;

namespace Mpgsql.Benchmarks.Comparison;

// Optional diagnostic dependency, loaded only by the explicit profiling runner.
internal sealed class BatchProfilerControl
{
    private readonly Action _start, _stop, _begin, _finish;

    internal BatchProfilerControl(string kind, string? apiPath)
    {
        if (kind == "none")
        {
            _start = _stop = _begin = _finish = static () => { };
            Features = "No profiler (verification)";
            return;
        }
        if (apiPath is null)
        {
            throw new ArgumentException("--profiler-api is required for trace/memory.");
        }
        var assembly = LoadApi(apiPath);
        var memory = kind == "memory";
        var type = assembly.GetType("JetBrains.Profiler.Api." + (memory ? "MemoryProfiler" : "MeasureProfiler"), true)!;
        var features = type.GetMethod("GetFeatures", Type.EmptyTypes)!.Invoke(null, null)!;
        Features = features.ToString()!;
        var bits = Convert.ToUInt32(features);
        if ((bits & 1) == 0 || memory && (bits & 4) == 0)
        {
            throw new InvalidOperationException("Profiler is not ready or allocation control is unavailable: " + Features);
        }

        Action Bind(string name)
        {
            return type.GetMethod(name, Type.EmptyTypes)!.CreateDelegate<Action>();
        }

        if (memory)
        {
            var allocations = type.GetMethod("CollectAllocations", [typeof(bool)])!.CreateDelegate<Action<bool>>();
            _start = () => allocations(true);
            _stop = () => allocations(false);
            // Heap snapshots force GC; both are outside the execution intervals.
            _begin = _finish = Bind("GetSnapshot");
        }
        else
        {
            _start = Bind("StartCollectingData");
            _stop = Bind("StopCollectingData");
            _begin = static () => { };
            _finish = Bind("SaveData");
        }
        _stop();
    }
    internal string Features { get; }

    internal void Begin()
    {
        _begin();
    }
    internal void Start()
    {
        _start();
    }
    internal void Stop()
    {
        _stop();
    }
    internal void Finish()
    {
        _finish();
    }

    private static Assembly LoadApi(string path)
    {
        var absolute = Path.GetFullPath(path);
        var dependency = Path.Combine(Path.GetDirectoryName(absolute)!, "JetBrains.HabitatDetector.dll");
        if (!File.Exists(dependency))
        {
            throw new FileNotFoundException("Profiler API requires its HabitatDetector dependency alongside the API.", dependency);
        }
        Assembly.LoadFrom(dependency);
        return Assembly.LoadFrom(absolute);
    }

    internal static void VerifyApi(string path)
    {
        var assembly = LoadApi(path);
        foreach (var name in new[] {"MeasureProfiler", "MemoryProfiler"})
        {
            var type = assembly.GetType("JetBrains.Profiler.Api." + name, true)!;
            var features = type.GetMethod("GetFeatures", Type.EmptyTypes)!.Invoke(null, null)!;
            Console.WriteLine($"PASS {name}.GetFeatures(): {features}; dependencies resolved.");
        }
    }
}