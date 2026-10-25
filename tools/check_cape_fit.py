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
CENTRAL_FACE_HALF_WIDTH = 0.20
CENTRAL_FACE_Y_RANGE = (0.15, 0.42)
BROW_REACH_HEIGHTS = (0.50, 0.54, 0.58, 0.62)
BROW_EDGE_ASYMMETRY_HEIGHTS = (0.42, 0.46)


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


def original_hood_position(u: float, v: float) -> tuple[float, float, float]:
    """Previous hood profile, used only as an exact-triangle reach baseline."""
    theta_open = 1.25
    top_y, bottom_y, center_z = 0.75, -0.18, -0.02
    neck_join_y = -0.085
    theta = theta_open + u * (math.tau - 2.0 * theta_open)
    crown = math.sin(min(1.0, v / 0.44) * math.pi / 2.0)
    neck_start = (top_y - neck_join_y) / (top_y - bottom_y)
    lower = min(1.0, max(0.0, (v - neck_start) / (1.0 - neck_start)))
    lower = lower * lower * (3.0 - 2.0 * lower)
    width = 0.025 + 0.410 * crown + 0.030 * (1.0 - crown)
    depth = 0.030 + 0.400 * crown + 0.030 * (1.0 - crown)
    width += (0.34 - width) * lower
    depth += (0.235 - depth) * lower
    y = top_y - (top_y - bottom_y) * v
    return (width * math.sin(theta), y,
            center_z + depth * math.cos(theta) + 0.012 * math.sin(theta * 3.0) * math.sin(v * math.pi))


def original_hood_mesh() -> tuple[list[tuple[float, float, float]], list[int]]:
    """Reconstruct the previous 33x13+apex topology without reading stock data."""
    positions = [original_hood_position(col / 32.0, row / 12.0)
                 for row in range(13) for col in range(33)]
    positions.append((0.0, 0.765, -0.02))
    indices = []
    for row in range(12):
        for col in range(32):
            a = row * 33 + col
            b, d = a + 1, a + 33
            c = d + 1
            indices.extend((a, d, c, a, c, b))
    for col in range(32):
        indices.extend((429, col, col + 1))
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
    """Find each +Z aperture edge by actual ray/section hits, including a closed seam."""
    def edge_half_angle(positive_x: bool) -> float:
        def hit(half_angle: float) -> bool:
            angle = half_angle if positive_x else math.tau - half_angle
            return ray_shell_radius_at(section, angle) is not None

        epsilon = 1e-6
        if hit(0.0):
            if hit(epsilon):
                return 0.0
            low = epsilon
        else:
            low = 0.0
        high = math.pi
        require(hit(high), "hood section does not reach its rear shell")
        for _ in range(32):
            middle = (low + high) * 0.5
            if hit(middle):
                high = middle
            else:
                low = middle
        return low

    left = edge_half_angle(True)
    right = math.tau - edge_half_angle(False)
    require(0.0 <= left < math.pi < right <= math.tau,
            "measured hood section does not leave the forward (+Z) shell interval")
    return left, right


def ray_shell_radius_at(section: list[tuple[tuple[float, float], tuple[float, float]]],
                        angle: float) -> float | None:
    """Return the nearest positive hit on exact section segments, if present."""
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
    return min(intersections) if intersections else None


def shell_radius_at(section: list[tuple[tuple[float, float], tuple[float, float]]], angle: float) -> float:
    """Intersect a radial ray with the exact segments cut from GLB triangles."""
    radius = ray_shell_radius_at(section, angle)
    require(radius is not None, f"no hood triangle section covers angle {math.degrees(angle):.2f} degrees")
    return radius


def fit_metrics(helmet: list[tuple[float, float, float]],
                visor: list[tuple[float, float, float]],
                hood: list[tuple[float, float, float]],
                hood_indices: list[int]) -> dict:
    require(len(hood) == GRID_VERTICES + 1, f"hood has {len(hood)} vertices; expected 430")
    require(len(hood_indices) % 3 == 0, "hood index count is not divisible by three")
    # Vertically warped rows are allowed. Every measurement below intersects
    # the actual emitted triangles at the stock vertex's local y coordinate.
    require(max(point[1] for point in hood) > min(point[1] for point in hood),
            "hood has no vertical extent")

    section_cache = {}

    def section_at(y):
        key = round(y, 7)
        if key not in section_cache:
            section_cache[key] = horizontal_triangle_section(hood, hood_indices, y)
        return section_cache[key]

    def is_open(angle: float, section) -> bool:
        return ray_shell_radius_at(section, angle) is None

    def point_clearance(point: tuple[float, float, float], section, angle: float) -> float:
        surface_radius = shell_radius_at(section, angle)
        point_radius = math.hypot(point[0], point[2] - HOOD_CENTER_Z)
        return surface_radius - point_radius

    checks = []
    helmet_open = 0
    for point in helmet:
        angle = angle_from_forward(point[0], point[2] - HOOD_CENTER_Z)
        section = section_at(point[1])
        surface_radius = ray_shell_radius_at(section, angle)
        if surface_radius is None:
            helmet_open += 1
            continue
        point_radius = math.hypot(point[0], point[2] - HOOD_CENTER_Z)
        checks.append((surface_radius - point_radius, point, angle, "helmet"))

    visor_open = 0
    visor_covered = []
    central_face = []
    central_face_margins = []
    max_visor_half_angle = 0.0
    min_visor_opening = math.inf
    for point in visor:
        x, y, z = point
        angle = angle_from_forward(x, z - HOOD_CENTER_Z)
        max_visor_half_angle = max(max_visor_half_angle, min(angle, math.tau - angle))
        section = section_at(y)
        left_open, right_open = opening_at_height(section)
        min_visor_opening = min(min_visor_opening, left_open, math.tau - right_open)
        face_sample = (abs(x) <= CENTRAL_FACE_HALF_WIDTH
                       and CENTRAL_FACE_Y_RANGE[0] <= y <= CENTRAL_FACE_Y_RANGE[1])
        if is_open(angle, section):
            visor_open += 1
            if face_sample:
                margin = left_open - angle if x >= 0.0 else angle - right_open
                central_face_margins.append(margin)
        else:
            clearance = point_clearance(point, section, angle)
            checks.append((clearance, point, angle, "visor"))
            visor_covered.append((clearance, point, angle))
        if face_sample:
            central_face.append(is_open(angle, section))
    central_face_clear = bool(central_face) and all(central_face)

    crown_y = hood[-1][1]
    helmet_top_y = max(y for _, y, _ in helmet)
    crown_clearance = crown_y - helmet_top_y

    original_hood, original_indices = original_hood_mesh()
    original_sections = {}

    def original_section_at(y):
        key = round(y, 7)
        if key not in original_sections:
            original_sections[key] = horizontal_triangle_section(original_hood, original_indices, y)
        return original_sections[key]

    brow_reach = []
    for y in BROW_REACH_HEIGHTS:
        actual_section = section_at(y)
        original_section = original_section_at(y)
        actual_left, actual_right = opening_at_height(actual_section)
        old_left, old_right = opening_at_height(original_section)
        actual_front_z = max(point[1] for segment in actual_section for point in segment)
        old_front_z = max(point[1] for segment in original_section for point in segment)
        brow_reach.append({
            "heightMeters": y,
            "frontMostHoodZMeters": actual_front_z,
            "originalFrontMostHoodZMeters": old_front_z,
            "frontMostForwardReachMeters": actual_front_z - old_front_z,
            "positiveXOpenHalfAngleDegrees": math.degrees(actual_left),
            "negativeXOpenHalfAngleDegrees": math.degrees(math.tau - actual_right),
        })
    minimum_brow_reach = min(item["frontMostForwardReachMeters"] for item in brow_reach)
    maximum_brow_reach = max(item["frontMostForwardReachMeters"] for item in brow_reach)
    brow_edge_asymmetry = []
    for y in BROW_EDGE_ASYMMETRY_HEIGHTS:
        section = section_at(y)
        left_open, right_open = opening_at_height(section)
        brow_edge_asymmetry.append({
            "heightMeters": y,
            "positiveXOpenHalfAngleDegrees": math.degrees(left_open),
            "negativeXOpenHalfAngleDegrees": math.degrees(math.tau - right_open),
        })
    brow_edge_angular_asymmetry = max(abs(item["positiveXOpenHalfAngleDegrees"]
                                          - item["negativeXOpenHalfAngleDegrees"])
                                      for item in brow_edge_asymmetry)

    forehead_samples = []
    for point in helmet:
        x, y, z = point
        half_angle = min(angle_from_forward(x, z - HOOD_CENTER_Z),
                         math.tau - angle_from_forward(x, z - HOOD_CENTER_Z))
        if 0.49 <= y <= 0.64 and half_angle <= math.radians(75.0):
            section = section_at(y)
            angle = angle_from_forward(x, z - HOOD_CENTER_Z)
            forehead_samples.append(ray_shell_radius_at(section, angle) is not None)

    lower_row = [(hood[(ROWS - 1) * COLS + col][0], hood[(ROWS - 1) * COLS + col][1],
                  hood[(ROWS - 1) * COLS + col][2]) for col in range(COLS)]
    lower_halfwidth = max(abs(x) for x, _, _ in lower_row)
    lower_rear_depth = HOOD_CENTER_Z - min(z for _, _, z in lower_row)
    positive_hang = min(y for x, y, _ in lower_row if x > 0.10)
    negative_hang = min(y for x, y, _ in lower_row if x < -0.10)
    side_hang_difference = abs(positive_hang - negative_hang)
    rear_bottom = min(lower_row, key=lambda point: abs(point[0]))

    require(bool(checks), "no covered helmet or visor vertices were tested against the hood shell")
    require(bool(central_face), "stock visor has no samples in the requested central face region")
    checks.sort(key=lambda item: item[0])
    minimum, limiting_point, limiting_angle, limiting_part = checks[0]
    helmet_checks = [item for item in checks if item[3] == "helmet"]
    require(bool(helmet_checks), "no helmet vertices were tested against the hood shell")
    visor_clearances = [item[0] for item in visor_covered]
    helmet_clearances = [item[0] for item in helmet_checks]
    central_face_margin = min(central_face_margins) if central_face_margins else 0.0
    return {
        "stockSourceNote": "Local stock GLTF vertices only; not redistributed with the mod.",
        "clearanceMethod": "same-height radial gap from covered stock helmet/visor vertex to exact hood GLB triangle-shell intersection",
        "units": "meters",
        "helmetVertexCount": len(helmet),
        "visorVertexCount": len(visor),
        "hoodVertexCount": len(hood),
        "minimumOpeningHalfAngleAcrossVisorDegrees": math.degrees(min_visor_opening),
        "visorMaxHalfAngleDegrees": math.degrees(max_visor_half_angle),
        "visorVerticesVisibleThroughOpening": visor_open,
        "visorVerticesCoveredByCloth": len(visor_covered),
        "coveredVisorVerticesCheckedAgainstShell": len(visor_covered),
        "minimumCoveredVisorRadialClearanceMeters": min(visor_clearances) if visor_clearances else None,
        "minimumHelmetRadialClearanceMeters": min(helmet_clearances),
        "centralFaceRegion": {"maxAbsXMeters": CENTRAL_FACE_HALF_WIDTH,
                               "yRangeMeters": list(CENTRAL_FACE_Y_RANGE)},
        "centralFaceSampleCount": len(central_face),
        "centralFaceVisibleCount": sum(central_face),
        "centralFaceClear": central_face_clear,
        "centralFaceAngularMarginDegrees": math.degrees(central_face_margin),
        "foreheadHelmetRegion": {"yRangeMeters": [0.49, 0.64],
                                  "maxHalfAngleDegrees": 75.0},
        "foreheadHelmetSampleCount": len(forehead_samples),
        "foreheadHelmetCoveredCount": sum(forehead_samples),
        "foreheadHelmetCoverageRatio": (sum(forehead_samples) / len(forehead_samples)
                                         if forehead_samples else None),
        "helmetVerticesInOpenAperture": helmet_open,
        "helmetVerticesCheckedAgainstShell": len(helmet_checks),
        "minimumRadialShellClearanceMeters": minimum,
        "minimumClearancePointMeters": list(limiting_point),
        "minimumClearancePointAngleDegrees": math.degrees(limiting_angle),
        "minimumClearancePointPart": limiting_part,
        "shellClearanceP01Meters": checks[int((len(checks) - 1) * 0.01)][0],
        "crownVerticalClearanceMeters": crown_clearance,
        "cowlBottomHalfWidthMeters": lower_halfwidth,
        "cowlBottomRearDepthMeters": lower_rear_depth,
        "cowlRearBottomPointMeters": list(rear_bottom),
        "sideHangLowestYBySideMeters": {"positiveX": positive_hang, "negativeX": negative_hang},
        "sideHangDifferenceMeters": side_hang_difference,
        "browForwardReachVsOriginalMeters": brow_reach,
        "minimumBrowForwardReachMeters": minimum_brow_reach,
        "maximumBrowForwardReachMeters": maximum_brow_reach,
        "browEdgeAsymmetryHeightRangeMeters": [BROW_EDGE_ASYMMETRY_HEIGHTS[0],
                                                  BROW_EDGE_ASYMMETRY_HEIGHTS[-1]],
        "browEdgeAsymmetryMeasurements": brow_edge_asymmetry,
        "browEdgeAngularAsymmetryDegrees": brow_edge_angular_asymmetry,
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
                f"covered helmet/visor shell clearance {metrics['minimumRadialShellClearanceMeters']:.4f}m is below "
                f"{args.min_shell_clearance:.4f}m")
        if metrics["minimumCoveredVisorRadialClearanceMeters"] is not None:
            require(metrics["minimumCoveredVisorRadialClearanceMeters"] >= args.min_shell_clearance,
                    f"covered visor shell clearance {metrics['minimumCoveredVisorRadialClearanceMeters']:.4f}m is below "
                    f"{args.min_shell_clearance:.4f}m")
        require(metrics["crownVerticalClearanceMeters"] >= args.min_crown_clearance,
                f"crown clearance {metrics['crownVerticalClearanceMeters']:.4f}m is below "
                f"{args.min_crown_clearance:.4f}m")
        require(metrics["centralFaceClear"],
                f"central face view is obscured: {metrics['centralFaceVisibleCount']}/"
                f"{metrics['centralFaceSampleCount']} stock visor samples are open")
        require(metrics["centralFaceAngularMarginDegrees"] >= 5.0,
                f"central face opening margin {metrics['centralFaceAngularMarginDegrees']:.2f}° is too small")
        require(metrics["foreheadHelmetCoverageRatio"] is not None
                and metrics["foreheadHelmetCoverageRatio"] >= 0.50,
                f"forehead shell coverage is only {metrics['foreheadHelmetCoverageRatio']!r}")
        require(metrics["minimumBrowForwardReachMeters"] >= 0.055,
                f"brow forward reach is below the intended ~0.06m minimum: "
                f"{metrics['minimumBrowForwardReachMeters']:.3f}–"
                f"{metrics['maximumBrowForwardReachMeters']:.3f}m")
        require(metrics["browEdgeAngularAsymmetryDegrees"] >= 2.0,
                f"brow edge asymmetry is only {metrics['browEdgeAngularAsymmetryDegrees']:.1f}°; "
                "expected at least 2° between the measured asymmetric brow sections")
        require(0.04 <= metrics["sideHangDifferenceMeters"] <= 0.06,
                f"asymmetric side hang is {metrics['sideHangDifferenceMeters']:.3f}m; expected 0.04–0.06m")
        require(0.33 <= metrics["cowlBottomHalfWidthMeters"] <= 0.35,
                "hood lower cowl does not taper to the requested ~0.34m halfwidth")
        require(0.225 <= metrics["cowlBottomRearDepthMeters"] <= 0.245,
                "hood lower cowl does not taper to the requested ~0.235m rear depth")
        rear_x, rear_y, rear_z = metrics["cowlRearBottomPointMeters"]
        require(abs(rear_x) <= 0.02 and abs(rear_y + 0.18) <= 0.005 and abs(rear_z + 0.255) <= 0.005,
                "hood lower rear cowl no longer overlaps near y=-0.18m, z=-0.255m")
        if args.metrics_output:
            output = args.metrics_output
            if not output.is_absolute():
                output = root / output
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(metrics, indent=2) + "\n", encoding="utf-8")
        print("PASS: checked actual stock helmet/visor vertices against the generated hood mesh")
        print(f"  shell: helmet {metrics['helmetVerticesCheckedAgainstShell']} vertices, visor "
              f"{metrics['coveredVisorVerticesCheckedAgainstShell']} covered vertices; minimum gap "
              f"{metrics['minimumRadialShellClearanceMeters'] * 1000:.1f}mm"
              + (f" (covered visor {metrics['minimumCoveredVisorRadialClearanceMeters'] * 1000:.1f}mm)"
                 if metrics["minimumCoveredVisorRadialClearanceMeters"] is not None
                 else " (no stock visor samples under cloth)"))
        print(f"  face: {metrics['centralFaceVisibleCount']}/{metrics['centralFaceSampleCount']} central visor "
              f"samples visible; angular margin {metrics['centralFaceAngularMarginDegrees']:.1f}°; "
              f"{metrics['visorVerticesCoveredByCloth']} visor vertices covered")
        print(f"  brow: front shell advances {metrics['minimumBrowForwardReachMeters'] * 1000:.1f}–"
              f"{metrics['maximumBrowForwardReachMeters'] * 1000:.1f}mm across y=0.50–0.62m; "
              f"side angle difference peaks at {metrics['browEdgeAngularAsymmetryDegrees']:.1f}° "
              f"over y={BROW_EDGE_ASYMMETRY_HEIGHTS[0]:.2f}–{BROW_EDGE_ASYMMETRY_HEIGHTS[-1]:.2f}m; "
              f"forehead covers {metrics['foreheadHelmetCoveredCount']}/"
              f"{metrics['foreheadHelmetSampleCount']} shell samples")
        print(f"  crown/cowl: {metrics['crownVerticalClearanceMeters'] * 1000:.1f}mm crown extension; "
              f"rear bottom ({rear_x:.3f}, {rear_y:.3f}, {rear_z:.3f})m; "
              f"side-hang difference {metrics['sideHangDifferenceMeters'] * 1000:.1f}mm")
        if args.metrics_output:
            print(f"  fit report: {output}")
    except (OSError, ValueError, KeyError, TypeError, struct.error, check_cape_assets.CheckError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
