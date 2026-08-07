# First-Person View — Status

Per spec §11: what this is, how it was verified, and — importantly — what was **not** verified.

---

## Status

**Playable and played in a live session.** Walls, corridors, mouse-look, camera-relative movement,
half-height furniture, floor-flat structures and world interaction have all been exercised against a
running server. Performance, ghost/dead states, shuttles, rotated grids and top-down parity have
**not** been checked — see §5. Two visual bugs are open — see §6.

---

## 1. What it is

A Wolfenstein-style raycaster, toggled with **`J`**. Renders at a low internal resolution into a
render target and blits it upscaled with nearest-neighbour, so cost is independent of window size.

All work happens in the player's **grid-local** space. Tile indices and anchored lookups are
grid-local by definition, and a station can sit at an arbitrary position *and rotation*, so the
camera is converted into that frame once per frame in `FirstPersonRenderer`.

### Files — all under `Content.Client/FirstPerson/`

| File | Role |
|---|---|
| `FirstPersonCVars.cs` | CVar definitions. Separate `[CVarDefs]` class, which `Content.Shared/CCVar/CCVars.cs:12` tells forks to do. |
| `FirstPersonSystem.cs` | Mode state, toggle, camera position, camera-relative movement remapping. |
| `FirstPersonViewport.cs` | `UIWidget` owning the render target; mouse-look; blits upscaled. |
| `Camera/FirstPersonCamera.cs` | Camera state and projection (yaw, pitch-as-horizon-shift, fov, ray fan, world→camera). |
| `Render/FirstPersonRenderer.cs` | Runs the passes; converts the camera into grid-local space. |
| `Render/WallPass.cs` | Per-column DDA. Full-height columns and waist-height ones; `Depth[]` output. |
| `Render/FloorPass.cs` | Flat floor/ceiling bands. **Still a placeholder.** |
| `Render/EntityPass.cs` | Billboards, plus floor-plane projection for flat structures. |
| `Render/TileSolidityCache.cs` | Per-tile height class, cached per frame. |

### Modified elsewhere — wiring only

`Content.Shared/Input/ContentKeyFunctions.cs` (+1), `Content.Client/Input/ContentContexts.cs` (+1, in
the **`common`** context so the toggle works while ghosted), `Resources/keybinds.yml` (+3), and both
game screens get a sibling viewport with `Visible=False` and a `Wide` anchor preset.

No server code and no `RobustToolbox` changes.

---

## 2. CVars

| CVar | Default | Purpose |
|---|---|---|
| `firstperson.render_width` / `_height` | 640 / 360 | Internal render resolution. |
| `firstperson.fov` | 70 | Horizontal FOV, degrees. |
| `firstperson.max_range` | 32 | Max DDA distance, tiles. |
| `firstperson.eye_height` | 0.55 | Eye height above the floor, tiles. |
| `firstperson.mouse_sensitivity` | 0.3 | |
| `firstperson.mouse_capture` | true | Escape hatch if look misbehaves. |
| `firstperson.camera_relative_movement` | true | Off restores vanilla grid-relative WASD. |
| `firstperson.draw_entities` | true | Off isolates geometry from billboards. |
| `firstperson.draw_half_height` | true | Off isolates full-height walls from waist-height ones. |
| `firstperson.half_height` | 0.5 | Counter height, tiles. |

Set them with the `cvar` command — `cvar firstperson.draw_entities 0`. Typing the bare name does
nothing; it is not a command.

---

## 3. Decisions worth knowing

Each of these was a bug first. They are recorded because the wrong version looked reasonable.

1. **Geometry is selected by collision layer, not `OccluderComponent`.** Occluder means "blocks
   light", not "is a wall". `GlassLayer` is `WallLayer` minus `Opaque`, so every window, grille and
   glass airlock carries no occluder — using occluders left holes wherever the station used glass.
   `HighImpassable` selects full-height structures, `MidImpassable` waist-height ones. SS14 has no
   z-axis, but that bit pair is a usable two-level height model already present in the data.

2. **`WallPass` and `EntityPass` must stay exact complements.** Anything rasterised as geometry is
   excluded from the billboard pass. Otherwise every wall is drawn twice, the second time as a
   camera-facing card of its top-down sprite pasted over the correct column — which reads as
   corridors vanishing behind floating slabs.

3. **Mobs stay billboards; furniture does not.** Mob sprites are already drawn side-on with cardinal
   facings, which is exactly Doom's directional-sprite model. Furniture art is drawn from *above* and
   can never look right on a camera-facing card, so it has to be geometry.

4. **Floor-flat things are projected onto the floor plane.** Catwalks, lattice, carpets, puddles and
   exposed subfloor pipes (`DrawDepth.Puddles` and below) would otherwise stand up as a wall of
   grating in your face. They cannot simply be dropped — over open space a catwalk *is* the floor.

5. **Waist-height hits do not stop the ray and do not write `Depth[]`.** You can see over a counter,
   and something standing behind one must remain visible.

6. **Movement remaps keys; it does not rotate the movement frame.** `RotateCamera` only moves in 90°
   steps and the mover then glides toward that over `LerpTime`, so "forward" lagged the view by up to
   45° and slid. Instead the raw WASD press is swallowed and the grid direction the player meant is
   pressed through the normal predicted input pipeline. Two details are load-bearing:
   - Emitted commands use `replay: true`, or they collide with the physical key in `InputSystem`'s
     held-state table and get dropped as duplicates.
   - The handler passes through when `InputSystem.Predicted` is set. Prediction replays dispatched
     commands every tick; intercepting its own commands there applied movement for one frame and
     rolled it back, which showed up as flickering.
   - Key **releases** always reach the mover. If a key was held when the mode was toggled on, the
     mover holds that raw direction and the release is the only thing that will ever clear it.

7. **Mouse-look subtracts yaw.** `WallPass` fans rays from `Direction - Plane` on the left edge to
   `Direction + Plane` on the right, and `Plane` is forward turned clockwise, so screen-right is a
   *lower* yaw. Adding turned the view away from the mouse — and because that mirrors the world
   relative to the movement maths, it also inverted strafing while leaving forward/back correct.

8. **Walls are flat-shaded, not textured.** Every RSI is packed into one shared atlas, and routing
   that atlas through `DrawPrimitives` corrupts top-down rendering. Walls use `Texture.White` with
   per-vertex colour. SS14 has no side-elevation wall art anyway.

9. **Interaction needs two separate things, and neither works alone.**
   `GameplayStateBase.OnKeyBindStateChanged` is the single funnel every world interaction passes
   through — use, alt-use, examine, drag-drop, context menus — and it only resolves coordinates and a
   target when `args.Viewport` is non-null *and* implements `IViewportControl`.
   - The widget implements `IViewportControl`.
   - The widget calls `ViewportKeyEvent(this, args)` from `KeyBindDown`/`KeyBindUp`. `InputManager`
     passes **null** for the viewport when the UI declines a bind, so a control must nominate itself
     — exactly as `ScalingViewport` does. Implementing the interface without this changes nothing.

10. **The crosshair pick must exclude `Contained`.** `PixelToMap` marches a ray in 0.2-tile steps and
    returns the first point with an entity on it, capped by the wall depth for that column. The
    lookup's default flags include contained entities, and the player's own worn clothing sits at the
    player's own position — so the first step hit their shoes and *every click targeted those*. This
    is the same trap `EntityPass` already documents; it was walked into anyway.

11. **Floor corners nearer than the near plane are discarded, not clamped.** Screen Y goes as
    `height / depth`, so a corner just in front of the camera lands hundreds of pixels below the
    horizon while the tile's far corners sit normally — the quad becomes a long diagonal wedge that
    sweeps across the view as you walk. Clamping was tried and reverted. Losing the tile underfoot is
    much cheaper than the artifact.

### Deviations from the spec

- Default bind is **`J`**, not `V` — `V` is `OpenBackpack`, and `Shift/Ctrl/Alt+V` are all taken.
- `overrideDirection` takes `Direction`, not `RsiDirection`; `DrawEntity` resolves the RSI direction
  itself.
- No `FirstPersonComponent` and no `Camera/CameraInput.cs` — one local player, one camera, and
  mouse-look is four lines.
- `FloorPass` is two flat bands rather than per-tile colour. Still the placeholder the spec called M2.

---

## 4. Verified

Live session against a local server, playing as Captain:

- Walls, corridors and glass structures render with correct perspective.
- Mouse-look tracks the mouse; camera-relative movement walks where you look and steers cleanly.
- Counters render as short geometry; items on them sit on top rather than sinking.
- Catwalks and lattice read as floor rather than as grating in your face.
- **Interaction works.** Examine on a distant carpet returned "carpet — Fancy walking surface";
  examine on cloth resting on a counter returned "cloth — A raw material", i.e. it targets the object
  rather than the floor beneath it. Left-click pickup at that range correctly did nothing, being
  outside hand reach — pickup at close range is still unconfirmed.
- `dotnet build SpaceStation14.slnx` → 0 errors. `dotnet test Content.Tests` → 419 passed, 0 failed.
- Client passes the IL sandbox verifier. Note it rejects `stackalloc` (`localloc`) — that was caught
  the hard way.

---

## 5. NOT verified — read this part

1. **No performance measurement.** Spec §8 asks for a frame time at 640×360. None taken. The per-tile
   solidity cache exists precisely because a 640-column cast re-tests tiles constantly, but "designed
   not to be slow" is not "measured". Tracy is already wired in (`prof.tracy.enabled`).
2. **Untested states:** ghost, dead, inside a locker, round restart, no grid (in space), multiple
   grids, shuttles, rotated grids. The grid-local work was written *for* rotated grids but has never
   seen one.
3. **Top-down parity not proven.** The safety property "top-down is bit-identical to upstream" is
   argued — the renderer only reads ECS state and the mode is off by default — but not demonstrated.
   No parity harness exists.
4. **Toggle-stress and leak behaviour untested.** The render target is only reallocated on size
   change and disposed in `Dispose`, so it should be fine. Unverified.
5. **A shutdown assert recurs** — `EntityLookupSystem.RemoveChildrenFromTerminatingBroadphase`
   hitting `DebugTools.Assert` during map teardown on close. No first-person frames in the stack, and
   `DebugTools.Assert` compiles out of release builds. Probably not ours; cause unknown.
6. **`GetWorldToScreenMatrix` returns identity** because a perspective projection is not affine and
   does not fit a `Matrix3x2`. `MapTextOverlay` and `PopupOverlay` consume it, so floating text and
   popups will be misplaced in first person. `WorldToScreen` is correct point-wise.
7. **Close-range pickup is unconfirmed.** Examine is proven; putting an item in hand is not.

---

## 6. Open bugs

1. **You can walk through the waist-high geometry.** Confirmed to be *this code's* half-height
   surface — the brown `HalfBase` colour, drawn wherever `TileSolidityCache` reports
   `TileSolidity.Half` from `MidImpassable`. So a solid is being drawn where nothing actually blocks
   movement. Either the layer test is catching something that does not block mobs, or the tile is
   being attributed geometry from a neighbouring entity. Start by logging which entity supplies the
   `MidImpassable` fixture for the tile in front of the player.
   Bisect with `cvar firstperson.draw_half_height 0`.

2. **Red diagonal lines and dark quads appear over the view, shifting as you move.** Cause unknown.
   Ruled out: the flat pass and the wall pass, whose colours are fixed greys and a warm brown, and
   the `GetWorldToScreenMatrix` stub, which only `MapTextOverlay` and `PopupOverlay` consume and
   which would misplace *text*, not draw lines. Never reproduced in a driven session.
   Bisect with `cvar firstperson.draw_entities 0` then `cvar firstperson.draw_half_height 0` — if it
   survives both, nothing this code draws is responsible and it is an overlay on top.

### Method note

Both remaining bugs resisted being solved by reading code. The shoes bug above was found in three
cycles of driving the client directly — synthetic input via `keybd_event`/`mouse_event` plus a
temporary sawmill log — after two confident wrong guesses from static reading. Reach for that loop
earlier. Note `computer-use`'s `request_access` cannot see this client: it resolves against installed
Start-menu apps and SS14 runs from source.

---

## 7. Next steps, in order

1. **The two open bugs in §6**, walk-through-geometry first — it is a gameplay bug, not a cosmetic one.
2. **Floor casting.** `FloorPass` is still two flat colour bands, so the ground has no texture and no
   motion parallax — the single biggest thing making the view feel static while walking. The
   floor-plane projection in `EntityPass.DrawFlats` is a working prototype of the maths needed.
3. **A third height tier.** `MachineLayer` is `MidImpassable` without `HighImpassable`, so vending
   machines and lockers currently render waist-high.
4. **Per-column sprite clipping.** Billboards are culled all-or-nothing on their centre column, so
   mobs bleed through wall edges. `DrawEntity` does not expose the clipping needed to fix it properly.
5. Measure a frame time, then work the §5 list.

---

## 8. Running it

The dev server config is tracked and already set up for local work — lobby on, role timers off,
station events off. See the note at the top of `Content.Server/server_config.toml` about build
presets only supplying *defaults*, which is what made those three settings fight back.

```
dotnet build Content.Server -c Debug && ./bin/Content.Server/Content.Server.exe
dotnet build Content.Client -c Debug && ./bin/Content.Client/Content.Client.exe --connect --connect-address udp://localhost:1212
```

Then: close the guidebook, **Join**, pick Captain, press **`J`**.
