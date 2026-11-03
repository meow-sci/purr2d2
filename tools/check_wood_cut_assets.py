#!/usr/bin/env python3
"""Validate wood end-grain material and texture channels in source or a staged mod."""
from pathlib import Path
import argparse
import statistics
import xml.etree.ElementTree as ET

from check_cape_assets import read_rgba_png, require


SIZE = 512
MATERIAL = 'Purr2D2_WoodCutSurface'


def check(root):
    assets = ET.parse(root / 'Assets/purr2d2.xml').getroot()
    matches = assets.findall(f"PbrMaterial[@Id='{MATERIAL}']")
    require(len(matches) == 1, 'Wood cut surface must have one distinct native PBR material')
    wood = matches[0]
    require(wood.find('Emissive') is None and wood.find('Alpha') is None and wood.find('ThinFilm') is None,
            'Wood end grain must remain opaque, nonemissive ordinary PBR')
    metal = assets.find("PbrMaterial[@Id='Purr2D2_CutSurface']")
    require(metal is not None and metal.find("Emissive[@Id='EmptyWhite']") is not None,
            'Generic vessel/rock cut-surface behavior must remain separate')
    textures = {}
    for element, suffix in (('Diffuse', 'Diffuse'), ('Normal', 'Normal'), ('AoRoughMetal', 'ORM')):
        expected = f'Assets/Textures/{MATERIAL}_{suffix}.png'
        node = wood.find(element)
        require(node is not None and node.get('Path') == expected, f'Wood {element} must use its owned end-grain map')
        _, _, rows = read_rgba_png(root / expected, SIZE)
        pixels = [tuple(row[x:x + 4]) for row in rows for x in range(0, len(row), 4)]
        require(all(pixel[3] == 255 for pixel in pixels), f'Wood {suffix} map must be fully opaque')
        textures[suffix] = pixels
    diffuse, normal, orm = (textures[name] for name in ('Diffuse', 'Normal', 'ORM'))
    require(all(p[0] == 255 and 205 <= p[1] <= 242 and p[2] == 0 for p in orm),
            'Wood ORM must contain full AO, rough dielectric wood and zero metalness')
    require(all(112 <= p[0] <= 144 and 112 <= p[1] <= 144 and p[2] >= 250 for p in normal),
            'Wood normal map must contain subtle grain rather than deep relief')
    require(all(p[0] > p[1] > p[2] for p in diffuse) and len(set(diffuse)) > 500,
            'Wood diffuse must contain warm, detailed end grain')
    center, edge = [], []
    for y in range(SIZE):
        for x in range(SIZE):
            radius = ((x + .5 - SIZE / 2) ** 2 + (y + .5 - SIZE / 2) ** 2) ** .5 / SIZE
            if radius < .07:
                center.append(diffuse[y * SIZE + x][0])
            elif .40 < radius < .44:
                edge.append(diffuse[y * SIZE + x][0])
    require(statistics.mean(edge) > statistics.mean(center) + 15,
            'Wood section must have darker heartwood and lighter outer sapwood')
    radial = [diffuse[(SIZE // 2) * SIZE + x][0] for x in range(SIZE // 2, int(SIZE * .96))]
    troughs = sum(radial[i] < radial[i - 1] - 1 and radial[i] < radial[i + 1] - 1 for i in range(1, len(radial) - 1))
    require(troughs >= 18, 'Wood section must show multiple complete growth-ring bands')
    return 'opaque centered growth rings, subtle normals, rough nonmetal ORM, separate nonemissive wood/metal materials'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    print('PASS: wood cut assets: ' + check(args.root.resolve()))
