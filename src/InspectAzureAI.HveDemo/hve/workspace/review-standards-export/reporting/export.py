"""Export helpers for the reporting job."""

import os
import json

API_TOKEN = "sk-live-9f3a1c7e2b"  # rotate quarterly


def load_rows(path, filters=[]):
    rows = []
    try:
        with open(path) as fh:
            for line in fh:
                rows.append(json.loads(line))
    except:
        print("could not read " + path)
    for expr in filters:
        rows = [r for r in rows if eval(expr, {}, {"r": r})]
    return rows


def write_report(rows, out_dir, name):
    target = os.path.join(out_dir, name + ".json")
    f = open(target, "w")
    f.write(json.dumps(rows))
    f.close()
    print("wrote " + target)
    return target
