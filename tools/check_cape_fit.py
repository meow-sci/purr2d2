#!/usr/bin/env python3
"""Measure cape hood clearance against a local stock helmet and visor GLTF.

The stock character references are optional inputs and are never copied into the
mod.  Their native centimeter coordinates are converted to meters before this
check.  Example:

    python3 tools/check_cape_fit.py \
      --helmet-gltf /path/to/KSA_Cat_Helmet.gltf \
      --visor-gltf /path/to/KSA_Cat_Visor.gltf
"""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path

import check_cape_assets


COLS = 33
ROWS = 13
GRID_VERTICES = COLS * ROWS
STOCK_UNITS_PER_METER = 100.0
HOOD_CENTER_Z = -0.02
DEFAULT_MIN_SHELL_CLEARANCE = 0.025
DEFAULT_MIN_CROWN_CLEARANCE = 0.08
DEFAULT_MIN_VISOR_ANGLE_MARGIN = 0.05


def require(condition: bool, message: str) -> None:
    if not condition:
        raise check_cape_assets.CheckError(message)


def _accessor_from_external_gltf(doc: dict, binary: bytes, accessor_index: int) -> list[tuple[float, float, float]]:
    accessor = doc["accessors"][accessor_index]
    require(accessor.get("type") == "VEC3" and accessor.get("componentType") == 5126,
            "stock POSITION accessors must be float VEC3")
    view = doc["bufferViews"][accessor["bufferView"]]
    count = accessor["count"]
    stride = view.get("byteStride", 12)
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    require(stride >= 12 and start >= 0 and start + (count - 1) * stride + 12 <= len(binary),
            "stock POSITION accessor is outside its buffer")
    return [struct.unpack_from("<3f", binary, start + i * stride) for i in range(count)]


def stock_gltf_positions(path: Path, meters_per_unit: float) -> list[tuple[float, float, float]]:
    """Read original stock GLTF positions without modifying or copying the asset."""
    doc = json.loads(path.read_text(encoding="utf-8"))
    require(doc.get("asset", {}).get("version") == "2.0", f"{path}: expected glTF 2.0")
    identity = [1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1]
    for node in doc.get("nodes", []):
        matrix = node.get("matrix", identity)
        require(len(matrix) == 16 and all(abs(float(a) - b) <= 1e-8 for a, b in zip(matrix, identity)),
                f"{path}: unexpected non-identity stock node matrix")
        require(node.get("translation", [0, 0, 0]) == [0, 0, 0]
                and node.get("rotation", [0, 0, 0, 1]) == [0, 0, 0, 1]
                and node.get("scale", [1, 1, 1]) == [1, 1, 1],
                f"{path}: unexpected non-identity stock mesh transform")

    buffers = doc.get("buffers", [])
    require(len(buffers) == 1 and "uri" in buffers[0], f"{path}: expected one external stock buffer")
    uri = buffers[0]["uri"]
    require("://" not in uri and not Path(uri).is_absolute(), f"{path}: buffer URI must be local and relative")
    binary_path = (path.parent / uri).resolve()
    try:
        binary_path.relative_to(path.parent.resolve())
    except ValueError as exc:
        raise check_cape_assets.CheckError(f"{path}: buffer URI escapes its source directory") from exc
    binary = binary_path.read_bytes()
    positions = []
    for mesh in doc.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            accessor = primitive.get("attributes", {}).get("POSITION")
            require(accessor is not None, f"{path}: mesh primitive has no POSITION")
            positions.extend(_accessor_from_external_gltf(doc, binary, accessor))
    require(bool(positions), f"{path}: no stock mesh vertices found")
    return [tuple(float(component) / meters_per_unit for component in p) for p in positions]


def hood_mesh(path: Path) -> tuple[list[tuple[float, float, float]], list[int]]:
    doc, binary = check_cape_assets.read_glb(path)
    meshes = doc.get("meshes", [])
    require(len(meshes) == 1, f"{path}: expected one hood mesh")
    primitive = meshes[0]["primitives"][0]
    accessor = primitive["attributes"]["POSITION"]
    positions = check_cape_assets.accessor_values(doc, binary, accessor, "VEC3")
    indices = check_cape_assets.accessor_values(doc, binary, primitive["indices"], "SCALAR")
    return positions, indices


def cross2(a: tuple[float, float], b: tuple[float, float]) -> float:
    return a[0] * b[1] - a[1] * b[0]


def angle_from_forward(x: float, z: float) -> float:
    angle = math.atan2(x, z)
    return angle if angle >= 0 else angle + math.tau


def horizontal_triangle_section(positions: list[tuple[float, float, float]],
                                indices: list[int], y: float) -> list[tuple[tuple[float, float], tuple[float, float]]]:
    """Intersect every GLB triangle with the horizontal plane at y."""
    min_y = min(point[1] for point in positions)
    max_y = max(point[1] for point in positions)
    require(min_y - 1e-8 <= y <= max_y + 1e-8,
            f"helmet point y={y:.4f} lies outside hood vertical span [{min_y:.4f}, {max_y:.4f}]")
    epsilon = 1e-8
    segments = []
    for tri in range(0, len(indices), 3):
        triangle = [positions[indices[tri + offset]] for offset in range(3)]
        intersections = []
        for edge in range(3):
            a = triangle[edge]
            b = triangle[(edge + 1) % 3]
            da, db = a[1] - y, b[1] - y
            if abs(da) <= epsilon and abs(db) <= epsilon:
                intersections.extend(((a[0], a[2]), (b[0], b[2])))
            elif abs(da) <= epsilon:
                intersections.append((a[0], a[2]))
            elif abs(db) <= epsilon:
                intersections.append((b[0], b[2]))
            elif (da < 0.0 < db) or (db < 0.0 < da):
                fraction = da / (da - db)
                intersections.append((a[0] + (b[0] - a[0]) * fraction,
                                       a[2] + (b[2] - a[2]) * fraction))
        unique = []
        for point in intersections:
            if not any(math.hypot(point[0] - other[0], point[1] - other[1]) <= epsilon
                       for other in unique):
                unique.append(point)
        if len(unique) == 2:
            segments.append((unique[0], unique[1]))
        elif len(unique) == 3:
            # A horizontal face contributes its three actual mesh edges.
            segments.extend((unique[i], unique[(i + 1) % 3]) for i in range(3))
    require(bool(segments), f"hood mesh has no triangle section at y={y:.4f}")
    return segments


def opening_at_height(section: list[tuple[tuple[float, float], tuple[float, float]]]) -> tuple[float, float]:
    points = [point for segment in section for point in segment]
    angles = [angle_from_forward(x, z - HOOD_CENTER_Z) for x, z in points]
    left, right = min(angles), max(angles)
    require(0.0 < left < math.pi < right < math.tau,
            "measured hood section does not leave a symmetric forward (+Z) opening")
    return left, right


def shell_radius_at(section: list[tuple[tuple[float, float], tuple[float, float]]], angle: float) -> float:
    """Intersect a radial ray with the exact segments cut from GLB triangles."""
    direction = (math.sin(angle), math.cos(angle))
    intersections = []
    for start, end in section:
        a = (start[0], start[1] - HOOD_CENTER_Z)
        b = (end[0], end[1] - HOOD_CENTER_Z)
        edge = (b[0] - a[0], b[1] - a[1])
        denominator = cross2(direction, edge)
        if abs(denominator) <= 1e-12:
            continue
        radius = cross2(a, edge) / denominator
        t = cross2(a, direction) / denominator
        if radius >= -1e-8 and -1e-7 <= t <= 1.0000001:
            intersections.append(max(0.0, radius))
    require(bool(intersections), f"no hood triangle section covers angle {math.degrees(angle):.2f} degrees")
    return min(intersections)


def fit_metrics(helmet: list[tuple[float, float, float]],
                visor: list[tuple[float, float, float]],
                hood: list[tuple[float, float, float]],
                hood_indices: list[int]) -> dict:
    require(len(hood) == GRID_VERTICES + 1, f"hood has {len(hood)} vertices; expected 430")
    require(len(hood_indices) % 3 == 0, "hood index count is not divisible by three")
    row_ys = [hood[row * COLS][1] for row in range(ROWS)]
    require(all(row_ys[i] > row_ys[i + 1] for i in range(ROWS - 1)),
            "hood rows must descend monotonically from crown to neck")
    for row, y in enumerate(row_ys):
        require(all(abs(hood[row * COLS + col][1] - y) < 1e-6 for col in range(COLS)),
                f"hood row {row} is not a horizontal ring")

    section_cache = {}

    def section_at(y):
        key = round(y, 7)
        if key not in section_cache:
            section_cache[key] = horizontal_triangle_section(hood, hood_indices, y)
        return section_cache[key]

    visor_checks = []
    for x, y, z in visor:
        section = section_at(y)
        left_open, right_open = opening_at_height(section)
        angle = math.atan2(abs(x), z - HOOD_CENTER_Z)
        half_open = min(left_open, math.tau - right_open)
        visor_checks.append((half_open - angle, angle, half_open))
    visor_checks.sort(key=lambda item: item[0])
    visor_margin = visor_checks[0][0]
    max_visor_angle = max(item[1] for item in visor_checks)
    visor_opening = min(item[2] for item in visor_checks)
    crown_y = hood[-1][1]
    helmet_top_y = max(y for _, y, _ in helmet)
    crown_clearance = crown_y - helmet_top_y

    checks = []
    in_aperture = 0
    for x, y, z in helmet:
        angle = angle_from_forward(x, z - HOOD_CENTER_Z)
        section = section_at(y)
        left_open, right_open = opening_at_height(section)
        if angle < left_open or angle > right_open:
            in_aperture += 1
            continue
        surface_radius = shell_radius_at(section, angle)
        point_radius = math.hypot(x, z - HOOD_CENTER_Z)
        checks.append((surface_radius - point_radius, (x, y, z), angle))
    require(bool(checks), "no helmet vertices were tested against the hood shell")
    checks.sort(key=lambda item: item[0])
    minimum, limiting_point, limiting_angle = checks[0]

    lower_row = [(hood[(ROWS - 1) * COLS + col][0], hood[(ROWS - 1) * COLS + col][2])
                 for col in range(COLS)]
    lower_halfwidth = max(abs(x) for x, _ in lower_row)
    lower_rear_depth = HOOD_CENTER_Z - min(z for _, z in lower_row)
    return {
        "stockSourceNote": "Local stock GLTF vertices only; not redistributed with the mod.",
        "clearanceMethod": "same-height radial gap from stock vertex to exact hood GLB triangle-shell intersection",
        "units": "meters",
        "helmetVertexCount": len(helmet),
        "visorVertexCount": len(visor),
        "hoodVertexCount": len(hood),
        "hoodOpeningHalfAngleDegrees": math.degrees(visor_opening),
        "visorMaxHalfAngleDegrees": math.degrees(max_visor_angle),
        "visorAngularClearanceDegrees": math.degrees(visor_margin),
        "visorVerticesWithinOpening": all(item[0] >= 0.0 for item in visor_checks),
        "helmetVerticesInOpenAperture": in_aperture,
        "helmetVerticesCheckedAgainstShell": len(checks),
        "minimumRadialShellClearanceMeters": minimum,
        "minimumClearancePointMeters": list(limiting_point),
        "minimumClearancePointAngleDegrees": math.degrees(limiting_angle),
        "shellClearanceP01Meters": checks[int((len(checks) - 1) * 0.01)][0],
        "crownVerticalClearanceMeters": crown_clearance,
        "cowlBottomHalfWidthMeters": lower_halfwidth,
        "cowlBottomRearDepthMeters": lower_rear_depth,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1],
                        help="source or staged mod directory")
    parser.add_argument("--helmet-gltf", type=Path, required=True,
                        help="local KSA_Cat_Helmet.gltf; positions are native centimeters")
    parser.add_argument("--visor-gltf", type=Path, required=True,
                        help="local KSA_Cat_Visor.gltf; positions are native centimeters")
    parser.add_argument("--stock-units-per-meter", type=float, default=STOCK_UNITS_PER_METER,
                        help="native units per meter (default: 100 for KSA character GLTFs)")
    parser.add_argument("--min-shell-clearance", type=float, default=DEFAULT_MIN_SHELL_CLEARANCE,
                        help="minimum measured radial gap outside the aperture, in meters")
    parser.add_argument("--min-crown-clearance", type=float, default=DEFAULT_MIN_CROWN_CLEARANCE,
                        help="minimum hood apex extension above stock helmet crown, in meters")
    parser.add_argument("--min-visor-angle-margin", type=float, default=DEFAULT_MIN_VISOR_ANGLE_MARGIN,
                        help="minimum angle between visor extent and opening edge, in radians")
    parser.add_argument("--metrics-output", type=Path,
                        help="optional JSON file for the measured fit report")
    args = parser.parse_args()
    try:
        require(args.stock_units_per_meter > 0.0, "stock-units-per-meter must be positive")
        root = args.root.resolve()
        helmet = stock_gltf_positions(args.helmet_gltf, args.stock_units_per_meter)
        visor = stock_gltf_positions(args.visor_gltf, args.stock_units_per_meter)
        hood, hood_indices = hood_mesh(root / "Assets/Models/Purr2D2_Hood.glb")
        metrics = fit_metrics(helmet, visor, hood, hood_indices)
        require(metrics["minimumRadialShellClearanceMeters"] >= args.min_shell_clearance,
                f"helmet shell clearance {metrics['minimumRadialShellClearanceMeters']:.4f}m is below "
                f"{args.min_shell_clearance:.4f}m")
        require(metrics["crownVerticalClearanceMeters"] >= args.min_crown_clearance,
                f"crown clearance {metrics['crownVerticalClearanceMeters']:.4f}m is below "
                f"{args.min_crown_clearance:.4f}m")
        require(metrics["visorAngularClearanceDegrees"] >= math.degrees(args.min_visor_angle_margin),
                f"visor opening margin {metrics['visorAngularClearanceDegrees']:.2f}° is below "
                f"{math.degrees(args.min_visor_angle_margin):.2f}°")
        require(0.33 <= metrics["cowlBottomHalfWidthMeters"] <= 0.35,
                "hood lower cowl does not taper to the requested ~0.34m halfwidth")
        require(0.225 <= metrics["cowlBottomRearDepthMeters"] <= 0.245,
                "hood lower cowl does not taper to the requested ~0.235m rear depth")
        if args.metrics_output:
            output = args.metrics_output
            if not output.is_absolute():
                output = root / output
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(metrics, indent=2) + "\n", encoding="utf-8")
        print("PASS: checked actual stock helmet/visor vertices against the generated hood mesh")
        print(f"  shell: {metrics['helmetVerticesCheckedAgainstShell']} vertices; minimum radial clearance "
              f"{metrics['minimumRadialShellClearanceMeters'] * 1000:.1f}mm")
        print(f"  visor: max half-angle {metrics['visorMaxHalfAngleDegrees']:.2f}°, opening "
              f"{metrics['hoodOpeningHalfAngleDegrees']:.2f}°, margin "
              f"{metrics['visorAngularClearanceDegrees']:.2f}°")
        print(f"  crown: {metrics['crownVerticalClearanceMeters'] * 1000:.1f}mm; lower cowl "
              f"halfwidth {metrics['cowlBottomHalfWidthMeters']:.3f}m, "
              f"rear depth {metrics['cowlBottomRearDepthMeters']:.3f}m")
        if args.metrics_output:
            print(f"  fit report: {output}")
    except (OSError, ValueError, KeyError, TypeError, struct.error, check_cape_assets.CheckError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
