"""Export the frozen comparison results after measurement, never during it."""
import argparse
from collections import Counter
import csv
import json
from pathlib import Path


def read(path, expected_count=None):
    with path.open(encoding="utf-8-sig") as source:
        result = json.load(source)
    if not result.get("Complete", True):
        raise ValueError(f"Incomplete measurement: {path}")
    if expected_count is not None and len(result["Results"]) != expected_count:
        raise ValueError(f"Unexpected series count in {path}: expected {expected_count}")
    return result["Results"]


def export(path, rows, fields=None):
    if fields is None:
        fields = list(dict.fromkeys(key for row in rows for key in row))
    with path.open("w", encoding="utf-8-sig", newline="") as target:
        writer = csv.DictWriter(target, fields)
        writer.writeheader()
        writer.writerows(rows)


def scalar(row):
    return {key: value for key, value in row.items()
            if isinstance(value, (str, int, float, bool)) or value is None}


def meets(row):
    return row["Ratio95Upper"] <= 0.9 and row["MpgsqlNanoseconds"] / row["NpgsqlNanoseconds"] <= 0.9


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="artifacts/npgsql-10percent directory")
    parser.add_argument("--candidate", default="candidate3", help="TCP/concurrent snapshot directory")
    parser.add_argument("--converters-candidate", default="candidate3", help="Converter snapshot directory")
    parser.add_argument("--plot", action="store_true")
    args = parser.parse_args()
    current = args.root / args.candidate
    converter_current = args.root / args.converters_candidate
    out = args.root / "summary"
    out.mkdir(exist_ok=True)
    tcp = []
    for repeat in (1, 2):
        for row in read(current / f"tcp-paired-{repeat}.json", 21):
            tcp.append({"Repeat": repeat, **scalar(row)})
    export(out / "tcp.csv", tcp)
    export(out / "tcp-weak.csv", [row for row in tcp if row["Ratio95Upper"] > 0.9])
    export(out / "baseline-delta.csv", [scalar(row) for row in read(current / "baseline-delta.json", 21)])

    converters = read(converter_current / "converters-paired.json", 204)
    export(out / "converters.csv", [scalar(row) for row in converters])
    export(out / "converters-weak-full-matrix.csv", [scalar(row) for row in converters if not meets(row)])
    focused = []
    for name in ("focused-1", "focused-2", "bpchar-focused-1", "bpchar-focused-2"):
        for row in read(converter_current / f"{name}.json", 2 if name.startswith("bpchar") else 6):
            focused.append({"Run": name, **scalar(row)})
    export(out / "focused.csv", focused)
    verified_counts = Counter(
        (row["Shape"], row["Profile"], row["Operation"])
        for row in focused if meets(row)
    )
    focused_fail_keys = {
        (row["Shape"], row["Profile"], row["Operation"])
        for row in focused if not meets(row)
    }
    unresolved = [scalar(row) for row in converters
                  if not meets(row) and (verified_counts[(row["Shape"], row["Profile"], row["Operation"])] < 2
                                         or (row["Shape"], row["Profile"], row["Operation"]) in focused_fail_keys)]
    export(out / "converters-unresolved.csv", unresolved, list(scalar(converters[0])))

    concurrent = []
    for repeat in (1, 2):
        for row in read(current / f"concurrent-matched-{repeat}.json", 10):
            for name in ("InverseThroughput", "MeanLatency", "P99Latency", "AllocatedBytesPerRequest"):
                concurrent.append({"Repeat": repeat, "Profile": row["Profile"],
                                   "NativeDriver": row["NativeDriver"], "Metric": name,
                                   **scalar(row[name])})
    export(out / "concurrent.csv", concurrent)
    export(out / "concurrent-weak.csv", [row for row in concurrent
                                        if row["Metric"] != "AllocatedBytesPerRequest"
                                        and not row["MeetsTenPercentBound"]])
    export(out / "buffers.csv", [scalar(row) for row in read(args.root / "candidate2/buffers-paired.json", 16)])
    if (current / "cache-delta-1.json").exists():
        export(out / "cache-delta.csv", [{"Repeat": repeat, **scalar(row)}
                                        for repeat in (1, 2)
                                        for row in read(current / f"cache-delta-{repeat}.json", 4)])
    print(f"TCP: {len(tcp)} rows; full converters: {len(converters)}; unresolved converters: {len(unresolved)}")
    print(f"Summary: {out}")
    if args.plot:
        plot(args.root, out, current)


def plot(root, out, current):
    import matplotlib.pyplot as plt
    original = {(row["Case"], row["Path"]): row
                for row in read(root / "baseline/tcp-paired.json")}
    candidate = read(current / "tcp-paired-2.json")
    selected = [row for row in candidate
                if row["Path"] in ("ADO.NET/ReusedTyped", "ADO.NET/FreshBatchExecution")]
    fig, ax = plt.subplots(figsize=(10, 5.2), layout="constrained")
    for offset, color, label, lookup in (
        (-0.12, "#87919f", "Исходный Mpgsql", lambda row: original[row["Case"], row["Path"]]),
        (0.12, "#2965ae", "После оптимизации, повтор 2", lambda row: row),
    ):
        values = [lookup(row) for row in selected]
        means = [row["MpgsqlOverNpgsql"] for row in values]
        ax.errorbar(means, [i + offset for i in range(len(selected))],
                    xerr=[[row["MpgsqlOverNpgsql"] - row["Ratio95Lower"] for row in values],
                          [row["Ratio95Upper"] - row["MpgsqlOverNpgsql"] for row in values]],
                    fmt="o", color=color, capsize=3, label=label)
    ax.axvline(0.9, color="#259366", linestyle="--", label="Цель: ≤ 0.90")
    ax.axvline(1, color="#777777", linewidth=0.8)
    ax.set_xscale("log")
    ax.set_xticks([0.75, 0.9, 1, 1.25, 1.5, 2, 3, 4],
                 labels=["0.75", "0.90", "1", "1.25", "1.5", "2", "3", "4"])
    ax.set_yticks(range(len(selected)), labels=[row["Case"] for row in selected])
    ax.invert_yaxis()
    ax.set_xlabel("Mpgsql / Npgsql: парное геометрическое отношение latency; меньше — лучше")
    ax.set_title("Синтетический TCP: reused typed и Batch16, 95% CI")
    ax.grid(axis="x", alpha=0.15)
    ax.legend(loc="lower right", fontsize=8)
    fig.savefig(out / "tcp-ratios.png", dpi=180)
    plt.close(fig)


if __name__ == "__main__":
    main()
