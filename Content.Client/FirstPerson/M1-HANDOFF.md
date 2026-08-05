# M1 Handoff — First-Person View

Per spec §11, this states what changed, how it was verified, and — importantly — what was **not**
verified.

---

## Status

**Code complete and compiling. Not yet exercised in a running client.** The interactive
walk-around test that spec §8/M1 defines as the acceptance criterion has not been performed. Do not
treat M1 as done until it has.

---

## 1. What changed

### New files — all under `Content.Client/FirstPerson/`

| File | Reason |
|---|---|
| `FirstPersonCVars.cs` | CVar definitions (`firstperson.*`). Separate `[CVarDefs]` class, which is what `Content.Shared/CCVar/CCVars.cs:12` explicitly tells forks to do. |
| `FirstPersonSystem.cs` | Mode state, toggle keybind, per-frame camera position update, movement-frame sync. |
| `FirstPersonViewport.cs` | `UIWidget` owning the low-res render target; mouse-look; blits upscaled. |
| `Camera/FirstPersonCamera.cs` | Camera state and projection (yaw/pitch-as-horizon-shift/fov, ray fan, world→camera). |
| `Render/FirstPersonRenderer.cs` | Orchestrates passes; converts the camera into grid-local space. |
| `Render/WallPass.cs` | Per-column DDA raycast, wall column emission, `depth[]` output. |
| `Render/FloorPass.cs` | Flat floor/ceiling bands (M1 placeholder). |
| `Render/EntityPass.cs` | Billboard projection, direction selection, far-to-near sort, wall clipping. |
| `Render/TileSolidityCache.cs` | Per-frame tile solidity cache (see §4 note on the perf risk this addresses). |
| `M0-FINDINGS.md`, `M1-HANDOFF.md` | Documentation. |

### Modified files — wiring only, minimal

| File | Change |
|---|---|
| `Content.Shared/Input/ContentKeyFunctions.cs` | +1 line: `ToggleFirstPerson`. |
| `Content.Client/Input/ContentContexts.cs` | +1 line: register in the **`common`** context (not `human`), so the toggle works while ghosted/dead per spec §7.1. |
| `Resources/keybinds.yml` | +3 lines: default binding. |
| `Content.Client/UserInterface/Screens/DefaultGameScreen.xaml` (+`.xaml.cs`) | Sibling viewport, `Visible=False`, plus anchor preset. |
| `Content.Client/UserInterface/Screens/SeparatedChatGameScreen.xaml` (+`.xaml.cs`) | Same. |

No server code, no `RobustToolbox` changes, no refactors of existing systems. `git status` shows
exactly the 7 modified files above.

---

## 2. Deviations from the spec — each deliberate, none silent

1. **Default keybind is `J`, not `V`.** The spec said "Default binding: `V`. Confirm no conflict
   before assigning." Checked: `V` is already `OpenBackpack` (`Resources/keybinds.yml:270`), and
   `Shift+V`/`Ctrl+V`/`Alt+V` are also taken. `J`, `L`, `N`, `M` are the only fully-unbound letters;
   picked `J`. User-rebindable, so this is a low-stakes default.

2. **`overrideDirection` takes `Direction`, not `RsiDirection`.** Spec §5.4 said to quantise to
   `RsiDirection` via `LayerGetDirections`. The actual signature
   (`DrawingHandleScreen.cs:227-235`) takes `Shared.Maths.Direction?`, and `DrawEntity` resolves the
   RSI direction internally. So the code uses `Angle.GetDir()`
   (`RobustToolbox/Robust.Shared.Maths/Angle.cs:74`) and lets the engine do the per-layer
   `Dir1/Dir4/Dir8` work. Simpler and more correct than the spec's plan.

3. **No `FirstPersonComponent.cs`.** Spec §4 lists it as a "client-only marker + per-player camera
   state". There is exactly one local player and one camera, so the camera lives on
   `FirstPersonViewport` and the mode flag on `FirstPersonSystem`. An empty marker component would
   have been an abstraction with no consumer. Trivial to add if per-entity state ever appears.

4. **No `Camera/CameraInput.cs`.** Mouse-look is ~4 lines (`FirstPersonViewport.MouseMove`);
   a separate class for "multiply delta by sensitivity" wasn't warranted.

5. **All rendering happens in grid-local space, which the spec did not mention.** Tile indices and
   `GetAnchoredEntitiesEnumerator` are grid-local by definition, and a station can sit at an
   arbitrary world position *and rotation*. Feeding world coordinates into the DDA would break on any
   rotated grid (and on shuttles generally). `FirstPersonRenderer.Render` converts the camera into
   the player's grid frame once per frame and the passes work there throughout.

6. **`FloorPass` is two flat bands, not per-tile flat colours.** Spec §5.3 M1 says "flat colour per
   tile, sampled from the tile's texture". This does flat colour for the whole floor/ceiling. It is
   strictly a placeholder either way and the cheaper version was enough to establish the horizon;
   per-tile colour is a small extension when the palette extractor of §12.3 exists.

---

## 3. How it was verified

- **Baseline established first.** Clean checkout at the pinned commit built with **0 errors, 463
  warnings** before any of my code existed — so the numbers below are attributable.
- **`dotnet build SpaceStation14.slnx`** → **0 errors**, 303 warnings (full solution; different
  project set than the Content.Client-only number above). No new warnings introduced by these files
  after fixing the repo's `RA0051` convention (`[Dependency]` fields must not be `readonly`).
- **`dotnet test Content.Tests`** → **Passed: 419, Failed: 0, Skipped: 1**. No existing test was
  modified.
- **Server boots and listens.** `Content.Server` starts, runs migrations, starts a Sandbox round,
  and accepts connections on port 1212 (verified with `Test-NetConnection`).
- **Client boots.** `Content.Client` launches, passes the IL sandbox verifier (which is what would
  reject content code touching engine internals), creates its GL context, and opens its window.
- **One real bug found and fixed pre-test:** the viewport was added to a `LayoutContainer` without an
  anchor preset, which would have given it zero size and rendered nothing. `MainViewport` gets
  `SetAnchorPreset(..., LayoutPreset.Wide)`; mine now does too, in both screens.

### Environment change made along the way

The repo requires **.NET SDK 10.0.100** (`global.json`) and only 9.0.203 was installed — nothing
could build. Installed **10.0.302** via winget (satisfies `rollForward: latestFeature`), side by side;
the 9.x SDK is untouched.

---

## 4. What was NOT verified — read this part

Everything in this section is honest unknown, not hedging.

1. **Nothing has been seen on screen.** No frame of the first-person view has ever been rendered and
   looked at. Walls, floors, billboards, the toggle, mouse-look — all compile, none are known to
   work. The `dotnet build` + `dotnet test` evidence above says the code is *type-correct*, not that
   it is *right*. I could not drive the client myself: the computer-use tool resolves applications
   against the Start menu, and SS14 runs from source, so it never matched the window.

2. **The projection math is unexercised.** Column heights, the `1/depth` scale factor, the
   `PixelsPerMeter = 32` billboard divisor, and the horizon placement are all plausible and
   dimensionally sensible but have never produced a pixel. Expect at least one of the scale
   constants to be wrong on first run. This is the most likely source of "it renders, but looks
   wrong."

3. **Mouse-look delivery is an assumption.** `Control.MouseMove` receives `GUIMouseMoveEventArgs`
   with a `Relative` delta, and `IClydeWindow.SetRelativeMouseMode(true)` hides and confines the
   cursor. Whether UI mouse-move events still route to the control under a *captured* cursor was not
   confirmed. If look doesn't work, that is the first thing to check —
   `firstperson.mouse_capture false` disables capture as an escape hatch.

4. **The M0-4 movement-frame concern is still open at runtime.** The code goes through
   `InputSystem.HandleInputCommand` (the verified-correct path), but the actual claim — that
   repeated programmatic `CameraRotateLeft/Right` keeps client and server rotation converged with no
   snap-back — has not been observed in a live session. Watch `InputMoverComponent.RelativeRotation`
   in ViewVariables while turning. The hysteresis (`π/4`) and the 1-second lerp
   (`SharedMoverController.LerpRotation`, `SharedMoverController.cs:386`) mean the frame *glides*
   rather than snaps; per M0 that is inherent, not a bug, but whether it *feels* acceptable is a
   judgement that requires playing it.

5. **No performance measurement whatsoever.** Spec §8/M1 asks for a frame-time number at 640×360.
   None taken. The `TileSolidityCache` addresses the spec's §9.4 concern (per-column anchored-entity
   enumeration) by design, but "designed not to be slow" is not "measured".

6. **Untested states:** ghost, dead, inside a locker, round restart, no grid (in space — the wall and
   entity passes return early, floors still draw, which is untested), multiple grids, shuttles,
   rotated grids. The grid-local work in §2.5 was written *for* rotated grids but has never seen one.

7. **Toggle-stress and leak behaviour untested.** Spec asks for "50 toggles in 10 seconds without
   leaking render targets". The target is only reallocated on size change and disposed in
   `Dispose`, so it should be fine — unverified.

8. **Top-down parity not proven.** The spec's most important safety property ("top-down mode must be
   bit-identical to upstream") is *argued* — the renderer only reads ECS state, and the mode is off
   by default — but not *demonstrated*. The §12.3 parity harness does not exist.

---

## 5. Next steps, in order

1. Run the client, connect to `localhost`, join the round, press **`J`**. Look at what happens.
   Expect to iterate on scale constants.
2. Check the client log for exceptions on toggle.
3. If look feels wrong, try `firstperson.mouse_capture false` and compare.
4. Measure a frame time (spec §12.1: Tracy is already wired in, `prof.tracy.enabled`).
5. Then, and only then, consider M1 accepted.
