#!/usr/bin/env python3
"""Render workload_bench results as markdown, judged against the pre-registered thresholds."""

from __future__ import annotations

import argparse
import json


def verdict(value: float, threshold: float, lower_is_better: bool = True) -> str:
    if lower_is_better:
        return "PASS" if value <= threshold else "FAIL"
    return "PASS" if value >= threshold else "FAIL"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", default="results.json")
    args = ap.parse_args()

    doc = json.load(open(args.input))
    meta, results = doc["meta"], doc["results"]
    th = meta["thresholds"]

    out: list[str] = []
    out.append("# LadybugDB workload benchmark\n")
    out.append(
        f"Host: {meta['host']['cpu_count']} cores, {meta['host']['storage']}, "
        f"Python {meta['host']['python']}, ladybug {meta['host']['ladybug']}. "
        f"Seed {meta['seed']}, {meta['attrs_per_obj']} attributes per object.\n"
    )
    out.append("Thresholds were registered before measurement:\n")
    for k, v in th.items():
        out.append(f"- `{k}` = {v}")
    out.append("")

    sizes = sorted({r["size"] for r in results})

    out.append("## Single-mutation commit latency (ms)\n")
    out.append("One attribute set, committed on its own — the unit of work a MUSH command produces.\n")
    out.append("| model | size | p50 | p95 | p99 | max | vs threshold |")
    out.append("|---|---:|---:|---:|---:|---:|---|")
    for size in sizes:
        for r in [x for x in results if x["size"] == size]:
            s = r["single_write"]
            out.append(
                f"| {r['model']} | {size:,} | {s['p50']:.2f} | {s['p95']:.2f} | "
                f"{s['p99']:.2f} | {s['max']:.2f} | "
                f"{verdict(s['p99'], th['single_write_p99_ms'])} |"
            )
    out.append("")

    out.append("## Point lookup (ms)\n")
    out.append("One attribute by object + name — the most common MUSH read.\n")
    out.append("| model | size | p50 | p95 | p99 | vs threshold |")
    out.append("|---|---:|---:|---:|---:|---|")
    for size in sizes:
        for r in [x for x in results if x["size"] == size]:
            s = r["point_lookup"]
            out.append(
                f"| {r['model']} | {size:,} | {s['p50']:.3f} | {s['p95']:.3f} | "
                f"{s['p99']:.3f} | {verdict(s['p99'], th['point_lookup_p99_ms'])} |"
            )
    out.append("")

    out.append("## Read latency under continuous write (ms)\n")
    out.append("| model | size | p50 | p95 | p99 | vs threshold |")
    out.append("|---|---:|---:|---:|---:|---|")
    for size in sizes:
        for r in [x for x in results if x["size"] == size]:
            s = r["read_under_write"]
            if s["n"] == 0:
                out.append(f"| {r['model']} | {size:,} | — | — | — | not measured |")
                continue
            out.append(
                f"| {r['model']} | {size:,} | {s['p50']:.3f} | {s['p95']:.3f} | "
                f"{s['p99']:.3f} | {verdict(s['p99'], th['read_under_write_p99_ms'])} |"
            )
    out.append("")

    out.append("## Batching knee (mutations/sec by transaction size)\n")
    out.append(
        "If per-commit fixed cost dominates, batching rescues it. Speedup is "
        "batch-1000 rate ÷ batch-1 rate.\n"
    )
    out.append("| model | size | 1 | 10 | 100 | 1000 | speedup | vs threshold |")
    out.append("|---|---:|---:|---:|---:|---:|---:|---|")
    for size in sizes:
        for r in [x for x in results if x["size"] == size]:
            b = r["batch_rates"]
            if not b:
                continue
            one = b.get("1") or b.get(1) or 0
            k = b.get("1000") or b.get(1000) or 0
            sp = (k / one) if one else 0
            out.append(
                f"| {r['model']} | {size:,} | {one:,.0f} | "
                f"{b.get('10', b.get(10, 0)):,.0f} | {b.get('100', b.get(100, 0)):,.0f} | "
                f"{k:,.0f} | {sp:.1f}x | "
                f"{verdict(sp, th['batch_knee_min_speedup'], lower_is_better=False)} |"
            )
    out.append("")

    out.append("## Write concurrency (mutations/sec by writer count)\n")
    out.append("Conflicts are retries forced by the engine refusing a concurrent write.\n")
    out.append("| model | size | 1 | 2 | 4 | 8 | conflicts @8 | scales? |")
    out.append("|---|---:|---:|---:|---:|---:|---:|---|")
    for size in sizes:
        for r in [x for x in results if x["size"] == size]:
            c = r["concurrent_rates"]
            if not c:
                continue
            g = lambda n: c.get(str(n), c.get(n, 0))
            cf = r.get("concurrent_conflicts", {})
            cf8 = cf.get("8", cf.get(8, 0))
            scales = "yes" if g(8) > g(1) * 1.5 else "no"
            out.append(
                f"| {r['model']} | {size:,} | {g(1):,.0f} | {g(2):,.0f} | {g(4):,.0f} | "
                f"{g(8):,.0f} | {cf8:,} | {scales} |"
            )
    out.append("")

    out.append("## Load, size and cold open\n")
    out.append("| model | size | load (s) | disk after load (MB) | disk after mutations (MB) | cold open (ms) |")
    out.append("|---|---:|---:|---:|---:|---:|")
    for size in sizes:
        for r in [x for x in results if x["size"] == size]:
            out.append(
                f"| {r['model']} | {size:,} | {r['load_s']:.2f} | {r['disk_mb_after_load']:.2f} | "
                f"{r['disk_mb_after_mutations']:.2f} | {r['cold_open_ms']:.2f} |"
            )
    out.append("")

    notes = [(r["model"], r["size"], n) for r in results for n in r.get("notes", [])]
    if notes:
        out.append("## Notes and errors\n")
        for m, s, n in notes:
            out.append(f"- **{m}** ({s:,}): {n}")
        out.append("")

    print("\n".join(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
