#!/usr/bin/env python3
"""Cost a directory of .eval logs against a model cost config.

Reads the token breakdown each eval log records (input, output, cache write, cache read, reasoning)
and prices it with the same file the engine uses, INSPECT_AZUREAI_MODEL_COST_CONFIG, a map of model
name to dollars per million tokens. Prints a table and, with --json, the rows.

    scripts/hve-cost-report.py logs/hve-matrix/logs --rates scripts/hve-cost-config.json

The engine's own accounting identity is asserted per row: total = input + output + cache write +
cache read, with reasoning tokens counted inside output. A row that does not satisfy it is reported
rather than silently priced.
"""
import argparse
import datetime
import glob
import json
import os
import zipfile


def load_runs(log_dir):
    runs = []
    for path in sorted(glob.glob(os.path.join(log_dir, "*.eval"))):
        header = json.loads(zipfile.ZipFile(path).read("header.json"))
        spec, stats = header["eval"], header.get("stats", {})
        meta = spec.get("metadata") or {}
        usage = list(stats.get("model_usage", {}).items())
        name, u = usage[0] if usage else (spec["model"], {})
        started, completed = stats.get("started_at"), stats.get("completed_at")
        seconds = None
        if started and completed:
            seconds = (datetime.datetime.fromisoformat(completed)
                       - datetime.datetime.fromisoformat(started)).total_seconds()
        runs.append({
            "log": os.path.basename(path),
            "model": name,
            "harness": meta.get("harness"),
            "framework": meta.get("framework"),
            # The resolved name says where it went: openai/azure/<dep> is Foundry, openai/<model> is direct.
            "direct": "/azure/" not in name and "/" in name,
            "input": u.get("input_tokens", 0),
            "output": u.get("output_tokens", 0),
            "cache_write": u.get("input_tokens_cache_write", 0),
            "cache_read": u.get("input_tokens_cache_read", 0),
            "reasoning": u.get("reasoning_tokens", 0),
            "total": u.get("total_tokens", 0),
            "seconds": seconds,
            "started": started,
        })
    runs.sort(key=lambda r: r["started"] or "")
    return runs


def price(run, rates):
    """Dollars for one run, or None when no rate covers its model."""
    rate = rates.get(run["model"])
    if rate is None:                       # try the bare deployment name behind a prefix
        rate = rates.get(run["model"].rsplit("/", 1)[-1])
    if rate is None:
        return None
    return (run["input"] * rate["input"]
            + run["output"] * rate["output"]
            + run["cache_write"] * rate["input_cache_write"]
            + run["cache_read"] * rate["input_cache_read"]) / 1e6


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log_dir")
    ap.add_argument("--rates", default=os.environ.get("INSPECT_AZUREAI_MODEL_COST_CONFIG"))
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    rates = {k: v for k, v in json.load(open(args.rates)).items() if isinstance(v, dict)} if args.rates else {}
    runs = load_runs(args.log_dir)

    for r in runs:
        counted = r["input"] + r["output"] + r["cache_write"] + r["cache_read"]
        r["accounting_ok"] = counted == r["total"]
        r["cost"] = price(r, rates)

    if args.json:
        print(json.dumps(runs, indent=1))
        return

    width = max([30] + [len(r["model"]) + 2 for r in runs])
    head = f"{'model':<{width}}{'harn':<8}{'fw':<6}{'in':>8}{'out':>7}{'cache w':>9}{'cache r':>9}{'total':>9}{'sec':>6}{'cost':>9}"
    print(head)
    print("-" * len(head))
    total = 0.0
    for r in runs:
        cost = "n/a" if r["cost"] is None else f"${r['cost']:.4f}"
        total += r["cost"] or 0.0
        flag = "" if r["accounting_ok"] else "  <- token counts do not sum to the reported total"
        print(f"{r['model']:<{width}}{r['harness'] or '-':<8}{r['framework'] or '-':<6}"
              f"{r['input']:>8,}{r['output']:>7,}{r['cache_write']:>9,}{r['cache_read']:>9,}"
              f"{r['total']:>9,}{r['seconds'] or 0:>6.0f}{cost:>9}{flag}")
    print("-" * len(head))
    print(f"{len(runs)} runs, {sum(r['total'] for r in runs):,} tokens, ${total:.4f}")
    if not rates:
        print("no rate file given, so nothing was priced; pass --rates or set INSPECT_AZUREAI_MODEL_COST_CONFIG")


if __name__ == "__main__":
    main()
