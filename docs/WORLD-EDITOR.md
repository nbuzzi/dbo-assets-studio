# World Editor

The World Editor opens a world with **the client's real world engine** (NtlPresentation in tool
mode) inside the Studio, and edits it on a private copy. Every save produces a candidate in a new
folder with a per-file diff; the client and its PAKs are only read.

## Tools

| Tool | What you can do |
|------|-----------------|
| Worlds & camera | List every world in `ter.pak`, prepare its cache, open it, free / top / front cameras, go to coordinates, engine stats overlay, captures |
| Spawns | Load the mob / NPC spawn tables of the world, pick on the terrain, add, move, rotate, delete, save as a new table |
| Terrain | Height brush (raise, lower, smooth, flatten), texture layer paint / erase, undo |
| Objects | Catalog from `objectproperty.rdf`, place, move, rotate, scale, delete; objects move between sectors when needed |
| Water | Create and remove water, height, scale, alpha, color, blend modes, sea / river sequences, depth map |
| Light, effects, sound | Light objects, world lights, effects and sound objects; field light and field sound properties |
| Field & tile props | Fog, sky, planets, bloom, color, weather, specular, heat haze; tile attributes and material brush |
| PVS | Rebuild the sector PVS for a selected area (only the recalculated records change) |
| Navigation | Hint for building server navigation from the edited workspace |

## How editing works

1. **Prepare cache** extracts the world (and shared terrain / object / effect data) from your
   client's `pack\` folder, read-only, into `%LOCALAPPDATA%\DBO Assets Studio\World Editor\cache`.
2. **Open for editing** copies the world into a private workspace; the engine runs on that copy.
3. **Save to workspace** writes the edited fields / sectors through the engine's own serializers.
4. **Export candidate** writes a new folder with the changed files, the originals and an audit; it
   checks that only what you edited changed.

Nothing is written into the client or the server.

## CLI

```text
dbo-assets world-engine worlds
dbo-assets world-engine prepare <world>
dbo-assets world-engine capture <world> <new.png> [--pos x,y,z] [--camera top|front|free] [--dist m] [--spawns spawn.rdf]
dbo-assets world-engine pick <world> <x,y,z> --spawns spawn.rdf
dbo-assets world-engine spawn-tables <world>
dbo-assets world-engine edit <world> <script.json> <new-candidate-folder>
dbo-assets world-engine verify <candidate> <script.json>
```

Edit scripts are in [`world-editor-scripts/`](../world-editor-scripts) and
[`examples/world-engine/`](../examples/world-engine).

## Setup

1. Build `world-host.exe` against **your own** client source tree (not included here). The engine
   must be built with the `Release4Tools` configuration of `NtlPresentation`, which defines
   `dNTL_WORLD_TOOL_MODE`:

   ```powershell
   .\build-world-host.ps1 -ClientSource C:\path\to\client-source -DirectXRoot C:\path\to\DirectX9 -ClientRoot C:\path\to\client -BuildToolLibs
   ```

   The exe and its DLLs go to `native\bin\world-host\` (or set `DBO_WORLD_HOST`).
2. In `studio.config.json` set `clientRoot` (folder with `pack\`) and, for spawns, `serverTables`
   (folder with `Table_World_Data.rdf` and the spawn tables).
3. `pak-tool.exe` and your `pakKey` are needed to read the PAKs.
