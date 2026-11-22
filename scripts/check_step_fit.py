"""Check placed, single-solid STEP instances at the named discrete poses."""
import argparse
from itertools import combinations
import json
import math
from pathlib import Path
import sys


def object_fields(value, allowed, label):
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be an object")
    unknown = value.keys() - set(allowed)
    if unknown:
        raise ValueError(f"{label} has unknown fields: {sorted(unknown)}")
    return value


def number(value, label):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{label} must be a finite number")
    return float(value)


def vector(value, label):
    if not isinstance(value, list) or len(value) != 3:
        raise ValueError(f"{label} must have three coordinates")
    return tuple(number(x, label) for x in value)


def named(value, label):
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{label} must be a nonempty string")
    return value


def unique_keys(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise ValueError(f"duplicate JSON key: {key}")
        obj[key] = value
    return obj


def load_spec(path):
    data = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_keys)
    object_fields(data, ("parts", "states", "min_clearances"), "manifest")
    parts = data.get("parts")
    if not isinstance(parts, dict) or len(parts) < 2:
        raise ValueError("provide at least two named part instances")
    for key, part in parts.items():
        named(key, "part ID")
        object_fields(part, ("step", "translate_mm", "rotation"), key)
        named(part.get("step"), f"{key}.step")
    states = data.get("states")
    if not isinstance(states, list) or not states:
        raise ValueError("provide at least one named state")
    names = set()
    for state in states:
        object_fields(state, ("name", "poses"), "state")
        name = named(state.get("name"), "state.name")
        if name in names:
            raise ValueError(f"duplicate state: {name}")
        names.add(name)
        for key, pose in object_fields(state.get("poses", {}), parts, name).items():
            object_fields(pose, ("translate_mm", "rotation"), key)
    gaps = {}
    items = data.get("min_clearances", [])
    if not isinstance(items, list):
        raise ValueError("min_clearances must be a list")
    for item in items:
        object_fields(item, ("pair", "mm"), "clearance")
        pair = item.get("pair")
        if not isinstance(pair, list) or len(pair) != 2 or any(not isinstance(k, str) or k not in parts for k in pair):
            raise ValueError("a clearance pair needs two known instance IDs")
        a, b = sorted(pair)
        mm = number(item.get("mm"), "clearance.mm")
        if a == b or (a, b) in gaps or mm < 0:
            raise ValueError(f"invalid or duplicate clearance pair: {pair}")
        gaps[a, b] = mm
    return parts, states, gaps


def load_solids(cq, directory, parts):
    cache = {}
    shapes = {}
    for key, part in parts.items():
        path = (directory / part["step"]).resolve(strict=True)
        if path not in cache:
            roots = cq.importers.importStep(str(path)).vals()
            solids = [solid for root in roots for solid in root.Solids()]
            if len(solids) != 1 or not solids[0].isValid() or solids[0].Volume() <= 0:
                raise ValueError(f"{path.name} needs one valid solid of positive volume")
            for entities in ("Faces", "Edges", "Vertices"):
                source_count = sum(len(getattr(root, entities)()) for root in roots)
                if source_count != len(getattr(solids[0], entities)()):
                    raise ValueError(f"{path.name} also contains geometry outside the solid ({entities})")
            cache[path] = solids[0]
        shapes[key] = cache[path]
    return shapes


def placed(shape, pose, label):
    rot = object_fields(pose.get("rotation", {}), ("axis", "degrees", "point_mm"), f"{label}.rotation")
    if "rotation" in pose:
        axis = vector(rot.get("axis"), f"{label}.axis")
        angle = number(rot.get("degrees"), f"{label}.degrees")
        if math.hypot(*axis) == 0:
            raise ValueError(f"{label} has a zero-length rotation axis")
        origin = vector(rot.get("point_mm", [0, 0, 0]), f"{label}.point_mm")
        end = tuple(a + b for a, b in zip(origin, axis))
        if angle:
            shape = shape.rotate(origin, end, angle)
    shift = vector(pose.get("translate_mm", [0, 0, 0]), f"{label}.translate_mm")
    return shape.translate(shift) if any(shift) else shape


def boxes_apart(a, b):
    return (a.xmax < b.xmin or b.xmax < a.xmin or a.ymax < b.ymin or b.ymax < a.ymin
            or a.zmax < b.zmin or b.zmax < a.zmin)


def check_state(parts, solids, state, minimums, volume_tol, length_tol):
    shapes = {}
    for key, source in solids.items():
        pose = {**parts[key], **state.get("poses", {}).get(key, {})}
        shapes[key] = placed(source, pose, f"{state['name']}/{key}")
    bounds = {key: shape.BoundingBox() for key, shape in shapes.items()}
    overlaps, clearances = [], []
    maximum = 0.0
    for a, b in combinations(sorted(shapes), 2):
        volume = 0.0
        if not boxes_apart(bounds[a], bounds[b]):
            common = shapes[a].intersect(shapes[b])
            if not common.isValid():
                raise ValueError(f"{state['name']}: invalid boolean result for {a}, {b}")
            volume = sum(s.Volume() for s in common.Solids())
            if not math.isfinite(volume) or volume < 0:
                raise ValueError(f"{state['name']}: invalid intersection volume for {a}, {b}")
        maximum = max(maximum, volume)
        if volume > volume_tol:
            overlaps.append({"pair": [a, b], "overlap_mm3": volume})
        if (a, b) in minimums:
            distance = 0.0 if volume > 0 else shapes[a].distance(shapes[b])
            if not math.isfinite(distance) or distance < 0:
                raise ValueError(f"{state['name']}: invalid distance for {a}, {b}")
            target = minimums[a, b]
            clearances.append({"pair": [a, b], "measured_mm": distance, "required_mm": target,
                               "passed": distance + length_tol >= target})
    return {"name": state["name"], "pairs_checked": math.comb(len(shapes), 2),
            "max_overlap_mm3": maximum, "overlaps": overlaps, "clearances": clearances,
            "passed": not overlaps and all(c["passed"] for c in clearances)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--volume-tol-mm3", type=float, default=0.0001)
    parser.add_argument("--length-tol-mm", type=float, default=0.000001)
    args = parser.parse_args()
    try:
        import cadquery as cq
        for tol in (args.volume_tol_mm3, args.length_tol_mm):
            if not math.isfinite(tol) or tol < 0:
                raise ValueError("numerical tolerances must be finite and nonnegative")
        parts, states, gaps = load_spec(args.manifest)
        solids = load_solids(cq, args.manifest.resolve().parent, parts)
        results = [check_state(parts, solids, state, gaps, args.volume_tol_mm3, args.length_tol_mm)
                   for state in states]
        passed = all(s["passed"] for s in results)
        report = {"status": "pass" if passed else "findings", "cadquery": cq.__version__,
                  "volume_tol_mm3": args.volume_tol_mm3, "length_tol_mm": args.length_tol_mm,
                  "instances": list(parts), "states": results}
        code = 0 if passed else 1
    except Exception as exc:
        report = {"status": "incomplete", "error": f"{type(exc).__name__}: {exc}"}
        code = 2
    print(json.dumps(report, ensure_ascii=not sys.stdout.isatty(), indent=2, allow_nan=False))
    return code


if __name__ == "__main__":
    sys.exit(main())
