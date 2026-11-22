"""Compare signed straight travel with local axle angles from a native assembly."""
import argparse
import csv
import json
import math
import sys
from pathlib import Path


def number(value):
    n = float(value)
    if not math.isfinite(n):
        raise ValueError("Expected a finite reading")
    return n


def run(args):
    if not math.isfinite(args.tolerance_mm) or args.tolerance_mm < 0:
        raise ValueError("Tolerance must be finite and non-negative")
    wheels = {}
    for name, radius, sign in args.wheel:
        radius, sign = number(radius), number(sign)
        if name in wheels or radius <= 0 or sign not in (-1, 1):
            raise ValueError("Each wheel needs a unique column, positive radius and sign +1 or -1")
        wheels[name] = (radius, sign)
    with args.csv.open(encoding="utf-8-sig", newline="") as file:
        reader = csv.DictReader(file)
        required = [args.travel, *wheels]
        if len(set(required)) != len(required) or any(k not in (reader.fieldnames or []) for k in required):
            raise ValueError("Missing or duplicate requested columns")
        samples = [[number(row[k]) for k in required] for row in reader]
    if len(samples) < 2:
        raise ValueError("Need at least two native samples")
    peak = {wheel: 0.0 for wheel in wheels}
    path_length = 0.0
    for index, (a, b) in enumerate(zip(samples, samples[1:]), 2):
        ds = b[0] - a[0]
        path_length += abs(ds)
        for i, (name, (r, direction)) in enumerate(wheels.items(), 1):
            if abs(ds / r) >= math.pi - 1e-7:
                raise ValueError(f"Interval ending at sample {index} spans at least half a turn of {name}. Sample more often.")
            angular_step = math.remainder(b[i] - a[i], 360.0)
            error = abs(ds - direction * r * math.radians(angular_step))
            peak[name] = max(peak[name], error)
    if path_length <= args.tolerance_mm:
        raise ValueError("Travel is too small to demonstrate rolling")
    passed = max(peak.values()) <= args.tolerance_mm
    return {"status": "pass" if passed else "fail", "samples": len(samples),
            "path_length_mm": path_length, "max_error_mm_by_wheel": peak}, int(not passed)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("csv", type=Path)
    p.add_argument("--travel", default="travel_mm", help="Signed CAD travel column in mm")
    p.add_argument("--wheel", nargs=3, action="append", required=True, metavar=("ANGLE_COLUMN", "RADIUS_MM", "SIGN"))
    p.add_argument("--tolerance-mm", type=float, default=0.05)
    try:
        result, code = run(p.parse_args())
    except (OSError, ValueError, TypeError, csv.Error) as exc:
        result, code = {"status": "incomplete", "error": str(exc)}, 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    sys.exit(code)
