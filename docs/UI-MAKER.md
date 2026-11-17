# UI Maker

UI Maker previews game windows **with the client's own GUI library** (NtlGui on RenderWare,
GFx 3.3 for Flash, the game's fonts), so what you see is what the game draws. It is meant for
iterating on a window before touching the client: build or import a layout, render it at several
resolutions, review the screenshots, approve, and only then generate code.

## What it can do

- **Layouts** (`*.uilayout.json`, schema `dbo-ui-layout/1`): panels, labels, buttons (with
  up/down/focus/disabled states), progress bars, icons, inputs, scrollbars, clip groups, `repeat`
  grids, Flash movies, strings, variants and hidden components.
- **Real render** through `ui-render.exe`: offscreen RenderWare camera, PNG output plus a metrics
  file with the screen rectangle of every component.
- **Import from the game**: any `.frm` window from `gui.pak` (with its `.srf` / `.rsr`) becomes an
  editable layout. The import is checked pixel by pixel against the window built from the original
  script; most windows come out identical.
- **Flash / GFx**: `.swf` movies from `flash.pak` render at a given frame or time.
- **Lint**: text that does not fit or is hidden, components off screen or outside their parent,
  overlaps, small hit targets, missing assets.
- **Review and approval**: `ui review` writes the PNGs and a `review.html`; `ui approve` records the
  layout hash. `ui codegen` refuses to generate C++ for a layout that changed after approval.
- **Codegen**: a header with rectangles, fonts, colors and a `Build()` that creates the NtlGui
  components.

## Studio

**Interfaz › UI Maker**: open/new/import, JSON editor with live render, drag or arrow keys to move
components, zoom, variants, lint list, and an **Original** toggle that shows the game's own build of
an imported window next to your layout.

## CLI

```text
dbo-assets ui init <name> <new.uilayout.json>
dbo-assets ui render <layout> [--variant v] [--size 1280x720] [--out file.png]
dbo-assets ui lint <layout>
dbo-assets ui review <layout> [--sizes 1280x720,1920x1080]
dbo-assets ui approve <layout> --hash <hash>
dbo-assets ui status <layout>
dbo-assets ui codegen <layout> <Name>Layout.generated.h
dbo-assets ui frm list [filter]
dbo-assets ui frm render <window.frm> --out file.png
dbo-assets ui import <window.frm> <new.uilayout.json>
dbo-assets ui swf <movie> --out file.png [--frame n | --time s]
```

Try it with [`examples/ui/sample-window.uilayout.json`](../examples/ui/sample-window.uilayout.json).

## Setup

1. Build `ui-render.exe` against **your own** client source tree (not included here):

   ```powershell
   .\build-ui-render.ps1 -ClientSource C:\path\to\client-source -DirectXRoot C:\path\to\DirectX9 -ClientRoot C:\path\to\client
   ```

   It is written to `native\bin\`. You can also point `DBO_UI_RENDER` to it.
2. Set `clientRoot` in `studio.config.json` (the client folder that contains `pack\`). `gui.pak`
   and `flash.pak` are extracted read-only to `%LOCALAPPDATA%\DBO Assets Studio\UI Maker`.
3. `pak-tool.exe` and your `pakKey` are needed to read the PAKs.

## Limits

- Particles, rotated surfaces and a few dynamic recolors are not drawn.
- Flash is rendered as a single frame.
- Exporting a layout back to `.frm` / `.srf` is not implemented yet.
