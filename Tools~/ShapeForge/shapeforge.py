#!/usr/bin/env python3
"""
ShapeForge · Kovu's Pride
Genera assets hipercasual a partir de figuras geométricas definidas con matemáticas (SVG) y los
convierte a PNG sin Inkscape. Un cuadrado, un círculo o una estrella no son de nadie: todo el arte
sale de primitivas, colores planos, degradados y sombras.

Uso:
    pip install resvg-py pillow
    python shapeforge.py spec.json -o Salida/ [--scale 1 2] [--only nombre] [--no-preview]

Genera, por cada asset: nombre.svg, nombre.png (y nombre@2x.png con --scale 1 2) y una hoja
_preview.png con todos los assets sobre cuadrícula, para revisarlos de un vistazo.
El formato del spec está documentado en README.md (ejemplos en examples/).
"""
import argparse
import json
import math
import os
import random
import sys
from xml.sax.saxutils import escape

# ───────────────────────────── Colores y paletas ─────────────────────────────

PALETTES = {
    # Colores planos y saturados típicos del hipercasual
    "hyper": {
        "red": "#FF4D5E", "orange": "#FF9F2E", "yellow": "#FFD93B", "green": "#4CD964",
        "teal": "#2ED1C4", "blue": "#3D8BFF", "purple": "#9B5CFF", "pink": "#FF6FB5",
        "white": "#FFFFFF", "cream": "#FFF6E5", "ink": "#2B2D42", "shadow": "#000000",
        "sky": "#BEE7FF", "grass": "#7BD957", "wood": "#C98B4F", "stone": "#9AA4B2",
    },
    "pastel": {
        "red": "#FF8A8A", "orange": "#FFC48A", "yellow": "#FFE89A", "green": "#9BE3A8",
        "teal": "#8FE3DC", "blue": "#9CC3FF", "purple": "#C6A8FF", "pink": "#FFB0D4",
        "white": "#FFFFFF", "cream": "#FFF8EE", "ink": "#4A4A5A", "shadow": "#000000",
    },
}


def hex_to_rgb(c):
    c = c.lstrip("#")
    if len(c) == 3:
        c = "".join(ch * 2 for ch in c)
    return tuple(int(c[i:i + 2], 16) for i in (0, 2, 4))


def rgb_to_hex(rgb):
    return "#%02X%02X%02X" % tuple(max(0, min(255, int(round(v)))) for v in rgb)


def shade(color, amount):
    """amount > 0 aclara hacia blanco, < 0 oscurece hacia negro (−1..1)."""
    r, g, b = hex_to_rgb(color)
    if amount >= 0:
        return rgb_to_hex((r + (255 - r) * amount, g + (255 - g) * amount, b + (255 - b) * amount))
    a = -amount
    return rgb_to_hex((r * (1 - a), g * (1 - a), b * (1 - a)))


class Ctx:
    def __init__(self, palette, seed):
        self.palette = palette
        self.rng = random.Random(seed)
        self.defs = []
        self.uid = 0

    def color(self, value):
        """Acepta '#RRGGBB', '$nombre' de la paleta y modificadores '$red+0.3' (aclarar) / '$red-0.2' (oscurecer)."""
        if value is None:
            return None
        if not isinstance(value, str) or not value.startswith("$"):
            return value
        name, amount = value[1:], 0.0
        for sign in ("+", "-"):
            if sign in name:
                name, amt = name.split(sign, 1)
                amount = float(amt) * (1 if sign == "+" else -1)
                break
        if name not in self.palette:
            raise ValueError(f"Color '{name}' no está en la paleta")
        base = self.palette[name]
        return shade(base, amount) if amount else base

    def new_id(self, prefix):
        self.uid += 1
        return f"{prefix}{self.uid}"


# ───────────────────────────── Geometría ─────────────────────────────

def fmt(v):
    return ("%.3f" % v).rstrip("0").rstrip(".")


def pts_to_path(points, closed=True):
    d = "M " + " L ".join(f"{fmt(x)} {fmt(y)}" for x, y in points)
    return d + (" Z" if closed else "")


def regular_polygon(cx, cy, r, sides, rotation=0.0):
    out = []
    for i in range(sides):
        a = math.radians(rotation - 90 + 360 * i / sides)
        out.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return out


def star_points(cx, cy, r_out, r_in, points, rotation=0.0):
    out = []
    for i in range(points * 2):
        r = r_out if i % 2 == 0 else r_in
        a = math.radians(rotation - 90 + 180 * i / points)
        out.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return out


def rounded_polygon_path(points, radius):
    """Polígono con esquinas redondeadas (arcos cuadráticos en cada vértice)."""
    if radius <= 0:
        return pts_to_path(points)
    n = len(points)
    d = []
    for i in range(n):
        p0, p1, p2 = points[i - 1], points[i], points[(i + 1) % n]
        v1 = (p0[0] - p1[0], p0[1] - p1[1])
        v2 = (p2[0] - p1[0], p2[1] - p1[1])
        l1, l2 = math.hypot(*v1), math.hypot(*v2)
        r = min(radius, l1 / 2, l2 / 2)
        a = (p1[0] + v1[0] / l1 * r, p1[1] + v1[1] / l1 * r)
        b = (p1[0] + v2[0] / l2 * r, p1[1] + v2[1] / l2 * r)
        d.append(("M" if i == 0 else "L") + f" {fmt(a[0])} {fmt(a[1])} Q {fmt(p1[0])} {fmt(p1[1])} {fmt(b[0])} {fmt(b[1])}")
    return " ".join(d) + " Z"


def smooth_closed_path(points):
    """Curva cerrada suave (Catmull-Rom a Bézier) que pasa por todos los puntos."""
    n = len(points)
    d = f"M {fmt(points[0][0])} {fmt(points[0][1])}"
    for i in range(n):
        p0, p1, p2, p3 = points[i - 1], points[i], points[(i + 1) % n], points[(i + 2) % n]
        c1 = (p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6)
        c2 = (p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6)
        d += f" C {fmt(c1[0])} {fmt(c1[1])} {fmt(c2[0])} {fmt(c2[1])} {fmt(p2[0])} {fmt(p2[1])}"
    return d + " Z"


def shape_path(s, ctx):
    """Devuelve (etiqueta, atributos) SVG de una primitiva."""
    t = s["type"]
    if t == "rect":
        x, y, w, h = s["x"], s["y"], s["w"], s["h"]
        r = s.get("radius", 0)
        return "rect", {"x": x, "y": y, "width": w, "height": h, "rx": r, "ry": r}
    if t == "circle":
        return "circle", {"cx": s["cx"], "cy": s["cy"], "r": s["r"]}
    if t == "ellipse":
        return "ellipse", {"cx": s["cx"], "cy": s["cy"], "rx": s["rx"], "ry": s["ry"]}
    if t == "polygon":                                   # lados regulares: triángulo, hexágono…
        pts = regular_polygon(s["cx"], s["cy"], s["r"], s["sides"], s.get("rotation", 0))
        return "path", {"d": rounded_polygon_path(pts, s.get("radius", 0))}
    if t == "rhombus":
        cx, cy, w, h = s["cx"], s["cy"], s["w"], s["h"]
        pts = [(cx, cy - h / 2), (cx + w / 2, cy), (cx, cy + h / 2), (cx - w / 2, cy)]
        return "path", {"d": rounded_polygon_path(pts, s.get("radius", 0))}
    if t == "star":
        pts = star_points(s["cx"], s["cy"], s["r"], s["r"] * s.get("inner", 0.5), s.get("points", 5), s.get("rotation", 0))
        return "path", {"d": rounded_polygon_path(pts, s.get("radius", 0))}
    if t == "points":                                    # polígono libre
        return "path", {"d": rounded_polygon_path([tuple(p) for p in s["points"]], s.get("radius", 0))}
    if t == "capsule":                                   # segmento con extremos redondos
        x1, y1, x2, y2, r = s["x1"], s["y1"], s["x2"], s["y2"], s["r"]
        a = math.atan2(y2 - y1, x2 - x1)
        nx, ny = -math.sin(a) * r, math.cos(a) * r
        d = (f"M {fmt(x1 + nx)} {fmt(y1 + ny)} L {fmt(x2 + nx)} {fmt(y2 + ny)} "
             f"A {fmt(r)} {fmt(r)} 0 0 0 {fmt(x2 - nx)} {fmt(y2 - ny)} L {fmt(x1 - nx)} {fmt(y1 - ny)} "
             f"A {fmt(r)} {fmt(r)} 0 0 0 {fmt(x1 + nx)} {fmt(y1 + ny)} Z")
        return "path", {"d": d}
    if t == "ring":
        cx, cy, ro, ri = s["cx"], s["cy"], s["r"], s["inner"]
        d = (f"M {fmt(cx - ro)} {fmt(cy)} A {fmt(ro)} {fmt(ro)} 0 1 0 {fmt(cx + ro)} {fmt(cy)} A {fmt(ro)} {fmt(ro)} 0 1 0 {fmt(cx - ro)} {fmt(cy)} Z "
             f"M {fmt(cx - ri)} {fmt(cy)} A {fmt(ri)} {fmt(ri)} 0 1 1 {fmt(cx + ri)} {fmt(cy)} A {fmt(ri)} {fmt(ri)} 0 1 1 {fmt(cx - ri)} {fmt(cy)} Z")
        return "path", {"d": d, "fill-rule": "evenodd"}
    if t == "sector":                                    # porción de círculo (tarta, abanico)
        cx, cy, r = s["cx"], s["cy"], s["r"]
        a0, a1 = math.radians(s["start"] - 90), math.radians(s["end"] - 90)
        large = 1 if (s["end"] - s["start"]) % 360 > 180 else 0
        d = (f"M {fmt(cx)} {fmt(cy)} L {fmt(cx + r * math.cos(a0))} {fmt(cy + r * math.sin(a0))} "
             f"A {fmt(r)} {fmt(r)} 0 {large} 1 {fmt(cx + r * math.cos(a1))} {fmt(cy + r * math.sin(a1))} Z")
        return "path", {"d": d}
    if t == "heart":                                     # curva paramétrica del corazón
        cx, cy, size = s["cx"], s["cy"], s["size"]
        pts = []
        for i in range(64):
            k = 2 * math.pi * i / 64
            x = 16 * math.sin(k) ** 3
            y = 13 * math.cos(k) - 5 * math.cos(2 * k) - 2 * math.cos(3 * k) - math.cos(4 * k)
            pts.append((cx + x * size / 34, cy - y * size / 34))
        return "path", {"d": pts_to_path(pts)}
    if t == "blob":                                      # forma orgánica suave, reproducible con seed
        rng = random.Random(s.get("seed", 1))
        n, cx, cy, r, j = s.get("lobes", 8), s["cx"], s["cy"], s["r"], s.get("jitter", 0.18)
        pts = []
        for i in range(n):
            a = 2 * math.pi * i / n
            rr = r * (1 + rng.uniform(-j, j))
            pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a) * s.get("squash", 1.0)))
        return "path", {"d": smooth_closed_path(pts)}
    if t == "line":                                      # trazo abierto (usa stroke)
        return "path", {"d": pts_to_path([tuple(p) for p in s["points"]], closed=False), "fill": "none"}
    if t == "path":                                      # SVG path a mano
        return "path", {"d": s["d"]}
    raise ValueError(f"Tipo de figura desconocido: {t}")


# ───────────────────────────── Estilo ─────────────────────────────

def paint(value, ctx):
    """Color plano o degradado: {'linear': [c1, c2], 'angle': 90} o {'radial': [c1, c2]}."""
    if value is None:
        return "none"
    if isinstance(value, dict):
        gid = ctx.new_id("g")
        stops = value.get("linear") or value.get("radial")
        stop_xml = "".join(
            f'<stop offset="{fmt(i / (len(stops) - 1))}" stop-color="{ctx.color(c)}"/>' for i, c in enumerate(stops))
        if "linear" in value:
            a = math.radians(value.get("angle", 90))          # 90 = de arriba (primer color) a abajo
            x1, y1 = 0.5 - math.cos(a) / 2, 0.5 - math.sin(a) / 2
            x2, y2 = 0.5 + math.cos(a) / 2, 0.5 + math.sin(a) / 2
            ctx.defs.append(f'<linearGradient id="{gid}" x1="{fmt(x1)}" y1="{fmt(y1)}" x2="{fmt(x2)}" y2="{fmt(y2)}">{stop_xml}</linearGradient>')
        else:
            cx, cy = value.get("center", [0.5, 0.5])
            ctx.defs.append(f'<radialGradient id="{gid}" cx="{fmt(cx)}" cy="{fmt(cy)}" r="{fmt(value.get("r", 0.6))}">{stop_xml}</radialGradient>')
        return f"url(#{gid})"
    return ctx.color(value)


def shadow_filter(sh, ctx):
    fid = ctx.new_id("f")
    ctx.defs.append(
        f'<filter id="{fid}" x="-50%" y="-50%" width="200%" height="200%">'
        f'<feDropShadow dx="{fmt(sh.get("dx", 0))}" dy="{fmt(sh.get("dy", 6))}" stdDeviation="{fmt(sh.get("blur", 0))}" '
        f'flood-color="{ctx.color(sh.get("color", "#000000"))}" flood-opacity="{fmt(sh.get("opacity", 0.25))}"/></filter>')
    return f"url(#{fid})"


def transform_attr(s):
    parts = []
    if "translate" in s:
        parts.append(f"translate({fmt(s['translate'][0])} {fmt(s['translate'][1])})")
    if "rotate" in s:
        pivot = s.get("pivot", [0, 0])
        parts.append(f"rotate({fmt(s['rotate'])} {fmt(pivot[0])} {fmt(pivot[1])})")
    if "scale" in s:
        sc = s["scale"] if isinstance(s["scale"], list) else [s["scale"], s["scale"]]
        parts.append(f"scale({fmt(sc[0])} {fmt(sc[1])})")
    return " ".join(parts)


def render_layer(s, ctx):
    """Una capa: figura simple o grupo ('group': [...]) con estilo y transformaciones."""
    attrs = {}
    tr = transform_attr(s)
    if tr:
        attrs["transform"] = tr
    if "opacity" in s:
        attrs["opacity"] = s["opacity"]
    if "shadow" in s:
        attrs["filter"] = shadow_filter(s["shadow"], ctx)

    if s.get("type") == "group" or "group" in s:
        inner = "".join(render_layer(child, ctx) for child in s.get("group", s.get("children", [])))
        return f"<g{attr_xml(attrs)}>{inner}</g>"

    tag, geo = shape_path(s, ctx)
    attrs.update(geo)
    if "fill" not in geo:
        attrs["fill"] = paint(s.get("fill", "$white"), ctx)
    st = s.get("stroke")
    if st:
        attrs["stroke"] = ctx.color(st.get("color", "$ink"))
        attrs["stroke-width"] = st.get("width", 4)
        attrs["stroke-linejoin"] = st.get("join", "round")
        attrs["stroke-linecap"] = st.get("cap", "round")
    xml = f"<{tag}{attr_xml(attrs)}/>"

    # Brillo superior típico del hipercasual: copia de la figura recortada a la mitad de arriba y aclarada
    hl = s.get("highlight")
    if hl:
        cid = ctx.new_id("c")
        hl = {} if hl is True else hl
        geo2 = dict(geo)
        ctx.defs.append(f'<clipPath id="{cid}"><{tag}{attr_xml(geo2)}/></clipPath>')
        x0, y0, x1, y1 = bounds_of(s)
        cut = y0 + (y1 - y0) * hl.get("height", 0.45)
        xml += (f'<g clip-path="url(#{cid})"{attr_xml({"transform": tr} if tr else {})}>'
                f'<rect x="{fmt(x0 - 10)}" y="{fmt(y0 - 10)}" width="{fmt(x1 - x0 + 20)}" height="{fmt(cut - y0 + 10)}" '
                f'fill="#FFFFFF" opacity="{fmt(hl.get("opacity", 0.25))}"/></g>')
    return xml


def bounds_of(s):
    t = s["type"]
    if t == "rect":
        return s["x"], s["y"], s["x"] + s["w"], s["y"] + s["h"]
    if t == "circle" or t in ("polygon", "star", "ring", "sector", "blob"):
        r = s["r"]
        return s["cx"] - r, s["cy"] - r, s["cx"] + r, s["cy"] + r
    if t == "ellipse":
        return s["cx"] - s["rx"], s["cy"] - s["ry"], s["cx"] + s["rx"], s["cy"] + s["ry"]
    if t == "rhombus":
        return s["cx"] - s["w"] / 2, s["cy"] - s["h"] / 2, s["cx"] + s["w"] / 2, s["cy"] + s["h"] / 2
    if t == "heart":
        h = s["size"] / 2
        return s["cx"] - h, s["cy"] - h, s["cx"] + h, s["cy"] + h
    if t == "capsule":
        r = s["r"]
        return min(s["x1"], s["x2"]) - r, min(s["y1"], s["y2"]) - r, max(s["x1"], s["x2"]) + r, max(s["y1"], s["y2"]) + r
    if t == "points" or t == "line":
        xs = [p[0] for p in s["points"]]
        ys = [p[1] for p in s["points"]]
        return min(xs), min(ys), max(xs), max(ys)
    return 0, 0, 0, 0


def attr_xml(attrs):
    return "".join(f' {k}="{escape(str(fmt(v) if isinstance(v, float) else v))}"' for k, v in attrs.items())


# ───────────────────────────── Salida ─────────────────────────────

def build_svg(asset, ctx):
    w, h = asset["width"], asset["height"]
    ctx.defs = []
    body = "".join(render_layer(layer, ctx) for layer in asset["layers"])
    defs = f"<defs>{''.join(ctx.defs)}</defs>" if ctx.defs else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">'
            f"{defs}{body}</svg>")


def render_png(svg_text, path, scale):
    import resvg_py
    data = resvg_py.svg_to_bytes(svg_string=svg_text, zoom=scale)
    with open(path, "wb") as f:
        f.write(bytes(data))


def preview_sheet(paths, out_path, cell=200):
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        print("Pillow no está instalado: sin hoja de previsualización (pip install pillow)")
        return
    cols = min(6, max(1, len(paths)))
    rows = (len(paths) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * cell, rows * (cell + 24)), (255, 255, 255, 255))
    draw = ImageDraw.Draw(sheet)
    for i, p in enumerate(paths):
        cx, cy = (i % cols) * cell, (i // cols) * (cell + 24)
        for yy in range(0, cell, 16):                    # cuadrícula para ver la transparencia
            for xx in range(0, cell, 16):
                if (xx // 16 + yy // 16) % 2:
                    draw.rectangle([cx + xx, cy + yy, cx + xx + 15, cy + yy + 15], fill=(236, 236, 236, 255))
        im = Image.open(p).convert("RGBA")
        im.thumbnail((cell - 16, cell - 16))
        sheet.alpha_composite(im, (cx + (cell - im.width) // 2, cy + (cell - im.height) // 2))
        draw.text((cx + 6, cy + cell + 4), os.path.splitext(os.path.basename(p))[0], fill=(40, 40, 40, 255))
    sheet.save(out_path)


def main():
    ap = argparse.ArgumentParser(description="ShapeForge: assets hipercasual con figuras geométricas (SVG → PNG)")
    ap.add_argument("spec", help="Archivo JSON con los assets")
    ap.add_argument("-o", "--out", default="ShapeForgeOutput", help="Carpeta de salida")
    ap.add_argument("--scale", type=float, nargs="+", default=[1.0], help="Escalas de PNG (p. ej. 1 2)")
    ap.add_argument("--only", nargs="*", help="Generar solo estos assets")
    ap.add_argument("--no-preview", action="store_true")
    args = ap.parse_args()

    with open(args.spec, encoding="utf-8") as f:
        spec = json.load(f)
    palette = dict(PALETTES.get(spec.get("palette", "hyper"), PALETTES["hyper"]))
    palette.update(spec.get("colors", {}))
    ctx = Ctx(palette, spec.get("seed", 1))
    os.makedirs(args.out, exist_ok=True)

    made = []
    for asset in spec["assets"]:
        if args.only and asset["name"] not in args.only:
            continue
        svg = build_svg(asset, ctx)
        base = os.path.join(args.out, asset["name"])
        with open(base + ".svg", "w", encoding="utf-8") as f:
            f.write(svg)
        for sc in args.scale:
            suffix = "" if sc == 1 else f"@{fmt(sc)}x"
            render_png(svg, base + suffix + ".png", sc)
        made.append(base + ".png")
        print("✓", asset["name"])

    if made and not args.no_preview:
        preview_sheet(made, os.path.join(args.out, "_preview.png"))
        print("Hoja de previsualización:", os.path.join(args.out, "_preview.png"))


if __name__ == "__main__":
    sys.exit(main())
