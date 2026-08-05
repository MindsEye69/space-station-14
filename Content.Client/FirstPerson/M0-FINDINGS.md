# M0 Findings — SS14 First-Person View

**Checked out against:** `space-station-14` @ `f4048e4dbe28362b7cacfe3481cf6ef5e7f8582d` (fork branch
`first-person-view`, remote `MindsEye69/space-station-14`), `RobustToolbox` submodule as pinned by
that commit: `724345afdffcdedebc43577654385a9ecfe3a092`.

**Drift note:** the spec was grounded against RobustToolbox `8d90700e7`, which is 4 commits *ahead*
of the `724345afd` this SS14 commit actually pins (`Remove redundant MsgEntity data #6906`, `Cache
texture UVs #6894`, `Fix Text Outline #6889`, `Cache PostShader render targets in Clyde #6657`).
Confirmed via `git merge-base --is-ancestor`: same line, no divergence. None of the 4 commits touch
anything in §2 of the spec. Not a blocker, noted per the spec's own "don't trust line numbers"
rule.

No production/renderer code was written for this milestone, per spec §8.

---

## M0-1: Does hiding `MainViewport` cleanly stop the 2D render, and what else breaks?

**Answer: yes, cleanly, with one expected and already-scoped-out breakage (click targeting).**

The render path and the game-logic path are more decoupled than the spec assumed — in our favor.

- `Content.Client/UserInterface/Controls/MainViewport.cs:14` (`MainViewport : UIWidget`) wraps a
  `ScalingViewport` (`Content.Client/Viewport/ScalingViewport.cs`), which draws via
  `ScalingViewport.Draw(IRenderHandle)` (`ScalingViewport.cs:163`). Standard Robust `Control` tree
  behavior: `Visible = false` stops `Draw` from being invoked for that subtree. No engine change
  needed to stop the 2D render.
- **PVS is unaffected**, confirmed architecturally: PVS is server-side entity-replication culling
  based on the player's eye position sent to the server; it has no dependency on what the client
  chooses to draw locally.
- **Occluder maintenance is unaffected.** `ClientOccluderSystem`
  (`RobustToolbox/Robust.Client/GameObjects/EntitySystems/ClientOccluderSystem.cs:21`) is a plain
  `EntitySystem` (extends `OccluderSystem`) whose `FrameUpdate(float frameTime)` override
  (`ClientOccluderSystem.cs:64`) is driven by the client's main game-loop frame tick, not by
  `Control.Draw()`. It keeps running whether or not `MainViewport` is visible. This is important:
  our raycaster depends on `OccluderComponent.Occluding` staying live, and it does.
- **Coordinate-conversion methods that don't depend on the mouse cursor keep working.**
  `IEyeManager.MainViewport` (`RobustToolbox/Robust.Client/Graphics/ClientEye/EyeManager.cs:48`) is
  a mutable `IViewportControl` reference, set once per screen-load in
  `Content.Client/UserInterface/Systems/Viewport/ViewportUIController.cs:74`
  (`_eyeManager.MainViewport = Viewport.Viewport;`) via the `Viewport` getter at line 21
  (`UIManager.ActiveScreen?.GetWidget<MainViewport>()`) — which finds the widget **by type,
  regardless of `Visible`**. So `WorldToScreen`, `MapToScreen`, `GetWorldViewbounds`, and
  `PixelToMap(Vector2)`/`ScreenToMap(Vector2)` (the *point*-taking overloads, not the
  `ScreenCoordinates`-taking ones) keep doing correct math against the hidden 2D viewport's
  projection the whole time first-person is active. Any HUD element that positions itself via
  `_eyeManager.WorldToScreen(...)` (name tags, floating combat text, etc.) will keep computing
  screen positions **as if the hidden top-down camera were still being looked through** — those
  positions will not correspond to where things actually appear in the first-person render. Not a
  blocker for M1 (look-and-walk only, no such overlays enabled), but flag it for M2/M3 if any HUD
  widget assumes `WorldToScreen` means "where the player currently sees it."
- **What does break, exactly as the spec anticipated:** `IEyeManager.ScreenToMap(ScreenCoordinates)`
  / `PixelToMap(ScreenCoordinates)` (`EyeManager.cs:142-163`) resolve the viewport via
  `_uiManager.MouseGetControl(point) is IViewportControl viewport` — i.e., "whatever control is
  under the mouse cursor, if it implements `IViewportControl`." If our `FirstPersonViewport` is the
  control under the cursor and does **not** implement `IViewportControl`, this silently returns
  `default(MapCoordinates)` rather than throwing. This is the entity-click/examine/context-menu
  path breaking, exactly as spec's open question #2 (§9) flagged. **Not needed for M1**
  (look-and-walk only). Cheap to fix in M2 — see M0-3.
- Lighting/FOV: the 2D lighting and FOV overlay computation is driven from within
  `Viewport.Render()` (`RobustToolbox/Robust.Client/Graphics/Clyde/Clyde.Viewport.cs`), called from
  `ScalingViewport.Draw()`. If `Draw()` never runs, that computation never runs either — which is
  exactly what we want (M1 explicitly has no lighting/FOV parity goal), and it does not affect
  occluder data (separate system, see above).

**Net effect:** hiding `MainViewport` is safe, and the only real breakage is click-targeting, which
M1 doesn't need and M2 can close cheaply (see M0-3).

---

## M0-2: Is relative mouse mode / cursor capture available?

**Answer: yes — this contradicts the spec's stated uncertainty.** The spec's §7.2 grep apparently
missed it (or it was added between whatever revision the spec's search covered and the current
commit).

- `IClydeWindow.SetRelativeMouseMode(bool enabled)`
  (`RobustToolbox/Robust.Client/Graphics/IClydeWindow.cs:32`), doc comment: *"While enabled, the
  cursor is hidden and confined to the window, and mouse motion continues to be reported when the
  cursor would otherwise reach the edge of the window."* This is exactly relative/FPS-style mouse
  capture.
- `IClydeWindow` is `public` (marked `[NotContentImplementable]`, meaning content can't write its
  *own* implementation of the interface — irrelevant here, we only need to *call* a method on an
  existing instance, not implement it).
- Getting a window reference from content is trivial: `Control.Window` is a base-class property
  (`RobustToolbox/Robust.Client/UserInterface/Control.Layout.cs:215`:
  `public virtual IClydeWindow? Window => Root?.Window;`). Every `Control`, including our own
  `FirstPersonViewport : Control`, has `.Window` for free.
- Relative motion deltas are already delivered per-event regardless of mode:
  `MouseMoveEventArgs.Relative` (`RobustToolbox/Robust.Client/Input/Events.cs:174`), a `Vector2` of
  "new position relative to the previous position."

**Consequence:** M1 does not need the spec's cursor-visible edge-offset fallback (§7.2 fallback).
True FPS-style mouselook — cursor hidden and confined, yaw/pitch driven by
`MouseMoveEventArgs.Relative` deltas — is directly implementable:
`FirstPersonViewport.Window?.SetRelativeMouseMode(true)` on entering first-person,
`SetRelativeMouseMode(false)` on leaving. This also resolves spec's stated concern about losing
HUD mouse interaction — since M1 is look-and-walk only with no HUD click-through requirement in
first-person, capturing the cursor is strictly fine, and the fallback is no longer needed as a
compromise.

**Not verified:** platform behavior differences (this is backed by SDL3
`SDL_SetWindowRelativeMouseMode` under the hood) — Linux/Wayland vs Windows vs macOS quirks around
cursor confinement are a known rough edge in other SDL-based engines. Test on the actual dev
machine in M1; don't assume parity across platforms without checking.

---

## M0-3: Can a content `Control` implement `IViewportControl`?

**Answer: yes, unambiguously.** This is the best news of the four.

`IViewportControl` (`RobustToolbox/Robust.Client/UserInterface/CustomControls/IViewportControl.cs:15`)
is a plain `public interface` with **no** `[NotContentImplementable]` attribute and no sealed/internal
gating. Its full surface is five members:

```csharp
IClydeWindow? Window { get; }
MapCoordinates ScreenToMap(Vector2 coords);
MapCoordinates PixelToMap(Vector2 point);
Vector2 WorldToScreen(Vector2 map);
Matrix3x2 GetWorldToScreenMatrix();
Matrix3x2 GetLocalToScreenMatrix();
```

All five are just projection math — the same camera-projection math `FirstPersonRenderer` already
needs for the raycaster and billboard passes (§5.1, §5.4 of the spec). `ScalingViewport` implements
this exact interface today (`Content.Client/Viewport/ScalingViewport.cs:311-372`) as a reference
example, and its doc comment confirms the intended purpose: *"This has to be implemented for
correct handling of input, you do not strictly need to implement this otherwise."*

**Consequence:** the M2 crosshair-interaction path (spec §1.4(B), §8/M2) is cheap, exactly as the
spec hoped but couldn't confirm. `FirstPersonViewport : Control, IViewportControl` can implement
`ScreenToMap`/`PixelToMap` as the inverse of the raycast projection (screen column → ray → world
point at some assumed depth, or intersect against `depth[]`), and `WorldToScreen` as the forward
projection already being computed per-entity in the billboard pass. Once that's wired,
`_uiManager.MouseGetControl(point) is IViewportControl` resolves correctly when the mouse is over
our control, restoring the click-targeting path M0-1 identified as broken — with the same
candidate-resolution semantics as top-down (spec §1.4(B)'s "reuse the exact same 'which entity is
under the cursor' resolution as top-down" claim holds).

Given how cheap this is, M1 should still defer it (scope discipline per §1 non-goals), but there's
no architectural reason M2 would be expensive.

---

## M0-4: Does §7.3 option A (camera-snap movement) work against grid-traversal snapping and lerping?

**Answer: the shared mechanism works as described, but there is one real subtlety the spec didn't
fully resolve — how first-person code should *trigger* it.**

**Confirmed exactly as spec claimed:**
- `SharedMoverController.RotateCamera(EntityUid uid, Angle angle)`
  (`Content.Shared/Movement/Systems/SharedMoverController.Input.cs:155-162`) adds `angle` to
  `mover.TargetRelativeRotation` and calls `Dirty(uid, mover)`, gated only by `CameraRotationLocked`.
- Bound via `CameraRotateInputCmdHandler` to `EngineKeyFunctions.CameraRotateLeft`/
  `CameraRotateRight` (same file, lines 42-43, 520-541), each a fixed `direction.ToAngle()` (±90°).
- `CameraRotationLocked` is fed from CVar `shuttle.camera_rotation_locked`, defaults `false`,
  `REPLICATED`.
- `InputMoverComponent.RelativeRotation`, `TargetRelativeRotation`, `RelativeEntity` are all in
  `InputMoverComponentState` and networked (`SharedMoverController.Input.cs:106-144`).

**The lerp and the grid-traversal snap are two independent mechanisms, not one — this matters:**

1. `LerpTarget` (`InputMoverComponent.LerpTarget`, a `TimeSpan`) only gates *when
   `TryUpdateRelative` re-runs after a grid change* — see
   `Content.Shared/Movement/Systems/SharedMoverController.cs:177-183`:
   `if (mover.LerpTarget < Timing.CurTime) TryUpdateRelative(uid, mover, xform);` — a one-second
   delay (`InputMoverComponent.LerpTime = 1.0f`) before the "snap to nearest cardinal" logic in
   `TryUpdateRelative` (`SharedMoverController.Input.cs:181-233`) fires after crossing grid/map
   boundaries.
2. Independently of that, `LerpRotation(EntityUid uid, InputMoverComponent mover, float frameTime)`
   (`SharedMoverController.cs:386-415`) runs **every tick, unconditionally** (called at line 183,
   right after the `LerpTarget` check, and also from `Content.Client/Replay/Spectator/
   ReplaySpectatorSystem.Movement.cs:53,68` for replay spectating). It eases `RelativeRotation`
   toward `TargetRelativeRotation` at a rate of `angleDiff * 5f * frameTime` (with a small minimum
   adjustment floor), i.e. an exponential ease, **not an instant snap** — a 90° turn takes on the
   order of half a second to a second to visually settle.

   **Consequence for feel:** whatever triggers `RotateCamera` (manual keypress today; our
   camera-yaw-crossing-a-90°-boundary trigger in M1), the *effective* movement frame
   (`GetParentGridAngle`, which `WASD` is interpreted in) doesn't snap instantly — it glides. This
   matches the spec's concern in §7.3 ("does lerping make the snap feel mushy") and the answer is:
   **it will be somewhat mushy by design**, not a bug to route around. Whether that's acceptable is
   a feel judgment call to make once it's actually walkable, not something to solve on paper.

**Not resolved by reading — needs an in-client test, flagging honestly per spec's own verification
standard (§11):** the spec's option A assumes we can drive this "automatically" — i.e., have
`FirstPersonSystem` call the equivalent of `RotateCamera` every time the continuous free-look yaw
crosses into a new 90°-bucket, without the player pressing a physical key. `RotateCamera` itself is
just a shared-code method call (`mover.TargetRelativeRotation += angle; Dirty(uid, mover);`) — it is
**not** inherently networked by itself. It only stays correctly predicted/replicated *today* because
it's invoked from `CameraRotateInputCmdHandler.HandleCmdMessage`, which runs identically on client
(immediate local prediction) and server (authoritative), because a real keypress produces a
`FullInputCmdMessage` that both sides receive via the normal input-command pipeline.

If `FirstPersonSystem` calls `_mover.RotateCamera(uid, angle)` directly from a **client-only**
system in response to yaw crossing a threshold, that mutation only happens locally — the server
never sees an equivalent input, so the server's copy of `InputMoverComponent` never rotates, and the
next incoming server state for that component will overwrite the client's local guess, producing a
visible snap-back. This would be a real, if subtle, prediction divergence — the kind of thing
§1.4(A)'s "byte-identical state" test would catch immediately.

**The fix is mechanical, not architectural:** don't call `RotateCamera` directly. Trigger it through
the *same* path a physical keypress uses — i.e., have `FirstPersonSystem` synthesize/raise the
existing `EngineKeyFunctions.CameraRotateLeft`/`CameraRotateRight` bound-key command through the
input-command pipeline (so both the local prediction and the networked message fire, exactly as a
real keypress would), rather than calling into `SharedMoverController` directly. This keeps §1.4(A)
intact and requires no server code — the server-side handler for these key functions already exists
and is unchanged.

**RESOLVED (spike completed, see below).** The content-facing API exists and content already uses it.

`InputSystem.HandleInputCommand(ICommonSession? session, BoundKeyFunction function,
IFullInputCmdMessage message, bool replay = false)` is **public**
(`RobustToolbox/Robust.Client/GameObjects/EntitySystems/InputSystem.cs:53`) on the public
`InputSystem` class (`:21`). It runs the command through the real pipeline: local predicted handling
plus `DispatchInputCommand` (`:139-143`), which calls `_stateManager.InputCommandDispatched(...)`
**and** `SendSystemNetworkMessage(message, message.InputSequence)` — i.e. the server receives an
ordinary input command, identical to a physical keypress. This is precisely the property M0-4
required.

Three independent precedents confirm the intended usage, two of them content-side:

- `Content.Client/ContextMenu/UI/EntityMenuUIController.cs:147-161` — builds a
  `ClientFullInputCmdMessage` from `_inputManager.NetworkBindMap.KeyFunctionID(func)` and calls
  `inputSys.HandleInputCommand(session, func, message)` to synthesize an interaction from a UI
  click. Same shape as what we need.
- `Content.Client/Interaction/DragDropSystem.cs:340` — replays a saved input command via the
  `replay: true` overload.
- `RobustToolbox/.../InputSystem.cs:162-210` (`GenerateInputCommand`, the `incmd` console command)
  — engine's own reference implementation, and a ready-made **manual test harness**: `incmd
  CameraRotateLeft Up` in the client console exercises the exact path before any of our code exists.

**Implementation shape for `FirstPersonSystem`** (no server change, no direct `RotateCamera` call):

```csharp
var func = EngineKeyFunctions.CameraRotateLeft;   // or CameraRotateRight
var funcId = _inputManager.NetworkBindMap.KeyFunctionID(func);
var msg = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, funcId)
{
    State = BoundKeyState.Up,   // NB: the handler acts on Up, not Down — see below
    Coordinates = Transform(player).Coordinates,
};
_inputSystem.HandleInputCommand(_playerManager.LocalSession, func, msg);
```

**Two gotchas found while reading, worth writing down now:**

1. `CameraRotateInputCmdHandler.HandleCmdMessage`
   (`Content.Shared/Movement/Systems/SharedMoverController.Input.cs:531-540`) early-returns unless
   `message.State != BoundKeyState.Up` is false — i.e. it **only acts on `BoundKeyState.Up`**.
   Sending `Down` silently does nothing. Easy to get wrong.
2. The handler applies a fixed `direction.ToAngle()` (±90°) per invocation (`:525-528`). Our
   free-look yaw snap must therefore emit *N discrete ±90° commands* to reach the target bucket, not
   one arbitrary-angle command. If the player spins fast enough to cross two boundaries in a frame,
   emit two commands. Arbitrary-angle rotation is precisely what spec §7.3 option B (M2) adds, and
   this constraint is the reason it's a separate milestone.

Remaining verification (deferred to M1 runtime, cannot be settled by reading): confirm in a running
client via `ViewVariables` that repeated programmatic rotation keeps client-predicted
`RelativeRotation` converged with the server's value and produces no snap-back. The `incmd` console
command makes this testable before writing any first-person code.

---

## Summary — M0 gate status

| # | Question | Status | Blocking for M1? |
|---|---|---|---|
| 1 | MainViewport hide consequences | Resolved — safe, click-targeting breaks as expected (fine, M1 doesn't click) | No |
| 2 | Mouse capture availability | Resolved — available, better than spec assumed (`IClydeWindow.SetRelativeMouseMode`) | No |
| 3 | `IViewportControl` implementability | Resolved — yes, cheap, plain public interface | No |
| 4 | Camera-snap movement (option A) | Mechanism confirmed; **one concrete follow-up spike needed**: verify the safe way to programmatically trigger `CameraRotateLeft/Right` through the real input-command path instead of calling `RotateCamera` directly | Recommended before wiring input, not before starting the renderer |

All four answers are more favorable than the spec anticipated except for the one item in M0-4,
which is a scoping/plumbing detail, not a redesign trigger. Nothing found here contradicts any hard
constraint in spec §1 (server untouched, no RobustToolbox changes, no refactors). Proceeding to M1
per spec §8 is reasonable, with the M0-4 input-dispatch spike as the first thing built (it's cheap
and de-risks the one open item before sinking time into `Render/`).
