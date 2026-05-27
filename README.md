# Vertex Paint Tool

A Unity 6 editor tool for painting vertex colours directly in the scene view, with support for `MeshRenderer` and `SkinnedMeshRenderer`.

## Features

- **Draggable overlay panel** — dock to any corner or drag freely
- **Four brush falloffs** — Hard, Linear, Soft, Gaussian
- **Quick colour presets** — R/G/B/W/Black swatches with active highlight
- **Shift+Click to erase**, Shift+Scroll / `[` `]` to resize brush
- **Fill Mesh** — flood-fill all vertices with the current colour
- **Undo/redo** — full Unity undo stack support
- **Non-destructive** — stores only changed vertices in a `VertexPaintData` asset; original mesh is never modified

## Installation

Open **Window → Package Manager**, click **+** → **Add package from git URL**, and paste:

```
https://github.com/zlotny/vertex-paint-tool.git
```

Requires Unity **6000.0** or later.

## Usage

1. Add a **Vertex Paint Tool** component to any GameObject with a `MeshRenderer` or `SkinnedMeshRenderer`.
2. Assign an **Original Mesh** and create a **Paint Data** asset.
3. Click **Rebuild Mesh** in the inspector.
4. Activate the **Vertex Paint Tool** from the scene view toolbar, then paint directly on the mesh.

## License

MIT — see [LICENSE.md](LICENSE.md).
