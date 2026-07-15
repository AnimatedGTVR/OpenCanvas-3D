# OpenCanvas 3D

OpenCanvas 3D is a free and open-source 2D/3D art application for Linux with
native X11 and Wayland support. Inspired by Microsoft's Paint 3D, it offers a
simple, beginner-friendly way to create artwork and 3D scenes, built in Godot.

At its core it paints directly onto a real 3D model surface: it raycasts
against the model mesh, interpolates UV coordinates, and draws into a live
SubViewport texture used as the model's material. Every paintable mesh (the
model and each placed 3D shape) gets its own independent canvas.

## Current Features

- Direct painting on a 3D model (default: a flat plane), with real mesh
  triangle UV hit detection and live texture updates
- Import your own model via the "Import Model" button, or drag and drop a
  `.glb`, `.gltf`, or `.obj` file onto the window
- Brush, eraser, fill (bucket), eyedropper, text, and 2D/3D shape tools
- 3D Doodle tool — draw a freehand stroke in the scene and it becomes a real
  extruded 3D tube mesh
- 3D shapes (cube, sphere, cone, cylinder, capsule, torus) can be painted on
  individually, each with its own canvas
- Undo/redo (Ctrl+Z / Ctrl+Y)
- 2D/3D canvas view toggle
- Brush type, thickness, and opacity controls, with a color palette
- Clear canvas, export painted texture to PNG
- Right-drag orbit camera, mouse wheel zoom

## Run

Open the project in Godot 4.6 with C# support, or build the scripts with:

```sh
dotnet build
```

Main scene:

```text
res://scenes/Main.tscn
```

## Controls

- Left drag on the model: paint (or fill/pick color/place a shape/doodle,
  depending on the selected tool)
- Right drag: orbit camera
- Mouse wheel: zoom
- Ribbon: brush, eraser, fill, eyedropper, doodle, text, 2D shapes, 3D
  shapes, clear, export, import model
- Side panel: brush/shape type, thickness, opacity, color
- Ctrl+Z / Ctrl+Y: undo / redo
