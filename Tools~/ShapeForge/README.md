# ShapeForge

Assets hipercasual a partir de figuras geométricas definidas con matemáticas → SVG → PNG.
Sustituye el flujo ChatGPT + Inkscape. Sin licencias de terceros: todo son primitivas.

## Instalación (una vez)

```
pip install resvg-py pillow
```

## Uso

```
python shapeforge.py examples/hypercasual.json -o Assets/MiJuego/Art/Generated --scale 1 2
```

- `-o` carpeta de salida (puede ser directamente dentro de `Assets/`; Unity importa los PNG).
- `--scale 1 2` genera `nombre.png` y `nombre@2x.png`.
- `--only coin star` regenera solo esos assets.
- `--no-svg` solo PNG; `--preview ruta.png` guarda la hoja de previsualización en otro sitio (útil si la salida es una carpeta `Resources`).
- Siempre crea `_preview.png` con todos los assets para revisarlos de un vistazo.

En Unity: Texture Type = Sprite (2D and UI). `Tools~` no se importa en los proyectos (la tilde lo oculta).

## Formato del spec (JSON)

```json
{
  "palette": "hyper",            // hyper | pastel
  "colors": { "gold": "#FFC531" },
  "assets": [
    { "name": "coin", "width": 128, "height": 128, "layers": [ ... ] }
  ]
}
```

Colores: `"#RRGGBB"`, `"$red"` (paleta), `"$red+0.3"` (aclara 30 %), `"$red-0.2"` (oscurece 20 %).

### Figuras (`type`)

| type | campos |
|---|---|
| rect | x, y, w, h, radius |
| circle | cx, cy, r |
| ellipse | cx, cy, rx, ry |
| polygon | cx, cy, r, sides, rotation, radius (esquinas) |
| rhombus | cx, cy, w, h, radius |
| star | cx, cy, r, inner (0–1), points, rotation, radius |
| points | points [[x,y]…], radius — polígono libre |
| capsule | x1, y1, x2, y2, r |
| ring | cx, cy, r, inner |
| sector | cx, cy, r, start, end (grados, 0 = arriba) |
| heart | cx, cy, size |
| blob | cx, cy, r, lobes, jitter, seed, squash — forma orgánica |
| line | points — trazo abierto (requiere stroke) |
| path | d — path SVG a mano |
| group | group: [capas] — para transformar varias juntas |

### Estilo (cualquier capa)

- `fill`: color o degradado `{"linear": ["$blue+0.3", "$blue-0.2"], "angle": 90}` / `{"radial": [...], "center": [0.5, 0.4]}`
- `stroke`: `{"color": "$ink", "width": 4}`
- `opacity`: 0–1
- `shadow`: `{"dx": 0, "dy": 6, "blur": 0, "opacity": 0.25}` (blur 0 = sombra sólida, estilo hipercasual)
- `highlight`: `true` o `{"height": 0.45, "opacity": 0.25}` — brillo plano en la parte superior
- `translate`, `rotate` + `pivot`, `scale`

## Flujo con Claude

1. Claude pide referencia (imagen online o boceto a mano) cuando la figura no es obvia.
2. Claude escribe el spec y genera `_preview.png`.
3. Se ajusta el JSON (no se redibuja a mano) y se regenera.
