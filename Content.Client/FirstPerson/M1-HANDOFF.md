# First-Person View — Status

Per spec §11: what this is, how it was verified, and — importantly — what was **not** verified.

---

## Status

**Playable and played in a live session.** Walls, corridors, mouse-look, camera-relative movement,
half-height furniture, floor-flat structures and world interaction have all been exercised against a
running server. Performance, ghost/dead states, shuttles, rotated grids and top-down parity have
**not** been checked — see §5. One visual bug is open — see §6.

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
| `Render/WallPass.cs` | Per-column DDA. Full-height columns, waist-height ones and their top caps; `Depth[]` output. |
| `Render/FloorPass.cs` | Flat floor/ceiling bands. **Still a placeholder.** |
| `Render/EntityPass.cs` | Billboards, plus floor-plane projection for flat structures. |
| `Render/TileSolidityCache.cs` | Per-tile height class and material colour, cached per frame. |
| `Render/SurfacePalette.cs` | Per-material top and side colours, sampled from sprites at RSI load. |

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
| `firstperson.half_height` | 0.32 | Counter height, tiles. Must stay below `eye_height`. |

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

11. **A collision layer says what a thing is; only its fixture shape says where it is.** Both passes
    treat a tile as all-or-nothing, so a structure is only geometry if its fixture covers the tile
    *centre*. Railings, fences, windoors, directional windows and edge firelocks are waist- or
    full-height by layer, but their fixture is a sliver against one edge — a railing is
    `-0.49,-0.49,0.49,-0.25`. Testing the layer alone filled the whole tile and drew a solid across
    three sides you can walk straight through. Size cannot separate the cases: a computer's fixture
    is half a tile across and a fence spanning the full width of one is a fifth of a tile deep, both
    smaller than that railing and both genuinely impassable. Leaving the middle of the tile clear is
    what edge-flush structures have in common, and it is also what makes them walkable. The test is
    taken in fixture-local space with an identity transform — anchored entities sit at the tile
    centre and rotate about it in 90° steps, so it needs no entity lookup and does not depend on
    which way the structure faces.

12. **Caps and faces can never overlap, which is why caps are whole tiles.** Faces are per-column
    strips but the top of a counter is drawn as one quad per tile, and that only works because the
    ordering is free. Everything projects as 1/depth, so a face at distance `d` covers
    `[horizon + (H-h)/d, horizon + H/d]` while a cap covers `[horizon + (H-h)/d_far, horizon +
    (H-h)/d_near]`: a tile's cap ends exactly where its own face begins, and any farther cap sits
    entirely above any nearer face. Caps are also coplanar with each other, so like floor tiles they
    project disjointly. One flat batch appended after the columns is therefore correct, with no
    merged depth sort. **This holds only while the eye is above the surface** — put
    `firstperson.eye_height` below `firstperson.half_height` and the two begin to overlap.

13. **One sprite answers two questions, and measuring is the only way to tell them apart.** The top
    tint is the *dominant* colour, the side tint is the mean of an *edge ring* just inside the opaque
    bounding box. The carpet table settles why they cannot be one number: its sprite is a wooden
    table under a green carpet, dominant `#006600`, rim `#4F2E18`. Both are right. A plain average
    gives `#30460F`, a colour present nowhere on the object.
    Three things were got wrong first, each fixed only because it was measured over all 433
    geometry-producing structure RSIs rather than the ten tables originally eyeballed:
    - *Dominant over every pixel lands on sprite linework.* SS14 art carries heavy near-black
      outlines and shading; on 13% of structures that won the bucket outright, so a bookshelf came
      out `#1C0B08` when it is plainly brown wood. Excluding pixels below luminance 30 takes it to
      **0%**. Restricting to the sprite's interior instead only reached 7% — the dark is spread
      through the shading, not confined to the border.
    - *"The rim is naturally darker than the top" is false.* It held on 10 of 10 tables, which is
      why it got written down as a discovered property; across all 433 it holds **41%**. The side is
      now forced below the top instead. That relationship is imposed, not found.
    - *The first sprite layer with an RSI is often not the object.* Airlocks and machines carry
      `_unlit` and `panel_closing` overlays that are a few pixels over a transparent field. The
      lookup picks the layer with the most opaque pixels, which is the object rather than the glow
      laid over it.

14. **Floor corners nearer than the near plane are discarded, not clamped.** Screen Y goes as
    `height / depth`, so a corner just in front of the camera lands hundreds of pixels below the
    horizon while the tile's far corners sit normally — the quad becomes a long diagonal wedge that
    sweeps across the view as you walk. Clamping was tried and reverted. Losing the tile underfoot is
    much cheaper than the artifact.

### The surface model — read before touching geometry

SS14 is drawn top-down, so every structure has art for exactly one face and none for the others.
That is a data problem, not a rendering one, and it has one answer applied everywhere:

> **The top face is drawn from the sprite. The sides are painted, never modelled.**

The *top* is the one face the game genuinely has correct art for — a top-down sprite is a picture of
it taken from precisely this angle. It is authoritative, not derived.

The *sides* have no art anywhere and never will, so they get a small library of painted profiles
carrying **fake depth in the paint** — a desk's kneehole, drawer seams, the shadow where it meets
the floor — tinted per entity. Trompe-l'œil, and it holds here for a specific reason: the viewing
envelope is tiny. The eye is fixed, there is no crouch or jump, and pitch is a horizon shear rather
than a rotation, so the player cannot acquire an angle that exposes a flat face. At 640×360 upscaled
with nearest-neighbour, the parallax a real 0.3-tile recess would add over a one-tile strafe is a
couple of pixels — under the resolution floor.

The usual objection to painted depth is occlusion: something *inside* the recess that paint cannot
correctly hide. That case does not exist here. With no z-axis nothing is ever under a desk; an item
on a desk tile shares coordinates with one on the floor, which is why `EntityPass` lifts it onto the
surface. There is no "under".

Do not give one structure real recessed geometry as a special case. The moment that happens the
renderer holds two incompatible models of what a surface is, and every later decision has to ask
which one applies. The value of the rule is that it is universal.

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
- **Counters have tops.** `WallPass.AppendCaps` lays a quad over each waist-height tile. Measured
  down a fixed column on a fixed counter, at `half_height` 0.5 the cap was **2px** against 94px of
  face; at 0.32 it is **10px** against 60px. Same tile, same shade (`#513A25` in both), so it is a
  like-for-like comparison — that is the difference between a slab and a table. 0.32 is now the
  default, and it is roughly physically right as well as legible: reading the 0.55 eye as a standing
  adult's 1.6m puts a tile at about 2.9m, making a real 0.9m counter 0.31.
- **Railings render as billboards, not as tile geometry.** Driven session, Dev map, `spawn Railing`
  laid in a row: the tiles read as railing sprites with floor visible through the gaps between the
  posts, where they were previously solid brown. Proved rather than eyeballed with the §6 bisect —
  `cvar firstperson.draw_entities 0` made the railings vanish completely, which they could not do if
  `WallPass` were still drawing them, and counters behind them stayed brown throughout. No
  exceptions in the client log.
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

1. **Red diagonal lines and dark quads appear over the view, shifting as you move.** Cause unknown.
   Ruled out: the flat pass and the wall pass, whose colours are fixed greys and a warm brown, and
   the `GetWorldToScreenMatrix` stub, which only `MapTextOverlay` and `PopupOverlay` consume and
   which would misplace *text*, not draw lines. Never reproduced in a driven session.
   Bisect with `cvar firstperson.draw_entities 0` then `cvar firstperson.draw_half_height 0` — if it
   survives both, nothing this code draws is responsible and it is an overlay on top.

### Fixed, with a caveat

**Walking through waist-high geometry** was `TileSolidityCache` testing collision layers without
looking at fixture shape — see decision 11. `CoversTileCentre` now gates both it and
`EntityPass.IsWallGeometry`; the two must keep applying it identically, or an edge-flush structure
falls out of both passes and becomes invisible.

The caveat: those structures are now *billboards*, confirmed in a driven session. A railing is drawn
as a camera-facing card of its top-down sprite rather than as a rail along the tile edge where it
actually stands — close up you see the posts but the rail between them sits off-frame. That is
honest about what blocks you, which the old behaviour was not, but it is not right yet. Drawing them
properly means sub-tile edge geometry in `WallPass` — testing the ray against the fixture AABB for
each tile it crosses, rather than treating tile entry as a hit.

Every prototype whose rendering this changed, found by listing fixture bounds that exclude the
origin: railings, edge-flush fences, `WindowDirectional`, windoors, `FirelockEdge` and barricade
edges. Fence *corners* keep a second centre-covering fixture and stay solid, and ordinary
full-tile firelocks were never affected.

### Method note

These bugs resist being solved by reading *code*. The shoes bug above was found in three cycles of
driving the client directly — synthetic input via `keybd_event`/`mouse_event` plus a temporary
sawmill log — after two confident wrong guesses from static reading. Note `computer-use`'s
`request_access` cannot see this client: it resolves against installed Start-menu apps and SS14 runs
from source.

**Send scancodes, not virtual keys.** This machine's layout is Norwegian (`00000414`), and
`keybd_event` with a hardcoded US virtual key silently lands on the wrong physical key —
`VK_OEM_3` mapped to scancode `0x27`, the `;` key, so every attempt to open the console typed a
radio prefix into chat instead. GLFW resolves keys from the lParam scancode regardless of VK, so
pass `KEYEVENTF_SCANCODE` with the US-position scancode (grave `0x29`, WASD `0x11/0x1E/0x1F/0x20`,
J `0x24`, Enter `0x1C`) and the layout stops mattering. A scancode of 0 is just as broken: GLFW maps
it to `GLFW_KEY_UNKNOWN` and the client drops the key. Typing *text* is the exception — derive those
keys from `VkKeyScan`, which is layout-correct by construction.

Useful setup for a driven session: `loginlocal = true` in `server_config.toml` means the local
player is full admin on join, so `spawn <prototype>` puts the exact case under test at your feet
rather than hunting the map for one. Toggling the console releases and re-acquires mouse capture and
the yaw drifts, so do not expect two screenshots either side of a `cvar` to share a heading — bisect
by what appears and disappears, not by comparing a fixed view.

The walk-through bug went the other way, and the distinction is worth keeping. Both guesses recorded
against it here were wrong, and so was a third — that some layer catches things which do not block
mobs, which `SharedPhysicsSystem.ShouldCollide` rules out, since it ORs the two layer/mask tests and
so any hard `MidImpassable` layer does block a humanoid. What settled it was reading the *data*: the
prototype fixture bounds. When the renderer's input is content rather than state, grep the YAML
before driving the client.

---

## 7. Next steps, in order

The surface work follows the rule in §3. Milestones 1 and 2 — top caps, and the height sweep that
set `half_height` to 0.32 — are **done**; what follows continues from there.

1. **Decide whether material accuracy is worth the legibility it costs.** `SurfacePalette` works and
   furniture is now per-material — but the flat brown it replaced was doing a job. It made furniture
   pop against grey walls at a glance, and a correctly-grey metal counter against a grey wall does
   not. This is a design call, not a bug, and everything below assumes an answer. Options: accept
   it; enforce a minimum contrast against the wall colour; sample the walls too so the whole scene is
   material-driven rather than half-and-half; or keep accurate hues but push saturation.
2. **Painted side profiles.** A small authored library — table, counter, crate, machine, plinth —
   with fake depth in the paint, selected per entity and tinted from 1. Note this does **not** run
   into decision 8: that constraint is about the shared *RSI atlas*, whereas an authored profile is
   a standalone PNG loaded as its own texture. Real textures are available here.
3. **Floor casting.** `FloorPass` is still two flat colour bands, so the ground has no texture and no
   motion parallax — the single biggest thing making the view feel static while walking. Independent
   of the surface work; interleave rather than block on it. `EntityPass.DrawFlats` and
   `WallPass.AppendCaps` are both working prototypes of the maths.
4. **Textured top caps.** Putting the actual sprite on the cap does hit decision 8 head-on. The way
   through is to stop borrowing the shared atlas: render each needed RSI state once into a render
   target we own, cache it, sample that. The expensive one, and the one that makes this look good
   rather than merely legible.
5. **A third height tier.** `MachineLayer` is `MidImpassable` without `HighImpassable`, so vending
   machines and lockers currently render waist-high. Deliberately after 2, so machines inherit the
   side-profile system instead of needing their own.
6. **The open bug in §6** — the red diagonals. Worth retrying as the passes above land: they add
   vertex batches, so it will either worsen or start reproducing reliably enough to diagnose.
7. **Sub-tile edge geometry**, so railings and windoors are drawn where they stand instead of as
   billboards. See the caveat in §6.
8. **Per-column sprite clipping.** Billboards are culled all-or-nothing on their centre column, so
   mobs bleed through wall edges. `DrawEntity` does not expose the clipping needed to fix it properly.
9. Measure a frame time, then work the §5 list.

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
