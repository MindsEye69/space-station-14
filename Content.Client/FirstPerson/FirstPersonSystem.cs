using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared.Input;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client.FirstPerson;

/// <summary>
/// Owns first-person mode: the toggle, the camera update, and swapping the 2D viewport out.
/// </summary>
/// <remarks>
/// This system is a pure consumer of client-side game state. It never authors game state, so it
/// cannot desync. The one exception — movement — deliberately goes through the normal predicted
/// input pipeline rather than mutating the mover directly.
/// </remarks>
public sealed class FirstPersonSystem : EntitySystem
{
    /// <summary>
    /// The four engine move functions, indexed by <see cref="North"/> and friends below.
    /// </summary>
    private static readonly BoundKeyFunction[] MoveFunctions =
    {
        EngineKeyFunctions.MoveUp,
        EngineKeyFunctions.MoveDown,
        EngineKeyFunctions.MoveLeft,
        EngineKeyFunctions.MoveRight,
    };

    private const int North = 0;
    private const int South = 1;
    private const int West = 2;
    private const int East = 3;

    [Dependency] private IUserInterfaceManager _uiMan = default!;
    [Dependency] private IPlayerManager _playerMan = default!;
    [Dependency] private IInputManager _inputMan = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private InputSystem _inputSystem = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private SharedMoverController _mover = default!;

    public bool Enabled { get; private set; }

    private FirstPersonViewport? _viewport;
    private MainViewport? _mainViewport;

    /// <summary>Which camera-relative keys the player is physically holding.</summary>
    private MoveKeys _heldKeys;

    /// <summary>Which of <see cref="MoveFunctions"/> this system currently has pressed.</summary>
    private readonly bool[] _emitted = new bool[4];

    /// <summary>
    /// Scratch for the directions we want pressed this frame. A field rather than a stackalloc
    /// because the content sandbox's IL verifier rejects localloc.
    /// </summary>
    private readonly bool[] _want = new bool[4];

    /// <summary>
    /// Set while this system is re-emitting a move command, so its own handler passes that
    /// command straight through to the mover instead of intercepting it again.
    /// </summary>
    private bool _emitting;

    private bool CameraRelativeMovement => _cfg.GetCVar(FirstPersonCVars.CameraRelativeMovement);

    public override void Initialize()
    {
        base.Initialize();

        var builder = CommandBinds.Builder
            .Bind(ContentKeyFunctions.ToggleFirstPerson,
                new PointerInputCmdHandler((in PointerInputCmdHandler.PointerInputCmdArgs _) =>
                {
                    Toggle();
                    return true;
                }, outsidePrediction: true))
            // handle: false is load-bearing. Shift is also Walk, and consuming the press here would
            // silently take walking away in first person.
            .Bind(ContentKeyFunctions.FirstPersonCursor,
                InputCmdHandler.FromDelegate(
                    _ => SetCursorFreed(true),
                    _ => SetCursorFreed(false),
                    handle: false,
                    outsidePrediction: true));

        // Must run ahead of the mover so it can swallow the raw WASD press and substitute the
        // grid direction the player actually meant. Passes through untouched while disabled.
        builder.BindBefore(EngineKeyFunctions.MoveUp, new MoveKeyHandler(this, MoveKeys.Forward), typeof(SharedMoverController));
        builder.BindBefore(EngineKeyFunctions.MoveDown, new MoveKeyHandler(this, MoveKeys.Back), typeof(SharedMoverController));
        builder.BindBefore(EngineKeyFunctions.MoveLeft, new MoveKeyHandler(this, MoveKeys.Left), typeof(SharedMoverController));
        builder.BindBefore(EngineKeyFunctions.MoveRight, new MoveKeyHandler(this, MoveKeys.Right), typeof(SharedMoverController));

        builder.Register<FirstPersonSystem>();

        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        CommandBinds.Unregister<FirstPersonSystem>();

        // Deliberately not SetEnabled(false). That releases the held move keys back through the
        // input pipeline, which raises events on the player and dispatches to the server — neither
        // of which is safe while the entity manager is flushing every entity out from under us.
        // There is nothing to hand back at this point anyway; the session is ending.
        Enabled = false;
        _heldKeys = MoveKeys.None;
        Array.Clear(_emitted);
    }

    private void OnPlayerDetached(LocalPlayerDetachedEvent ev)
    {
        // Never keep a first-person camera on an entity we no longer control.
        SetEnabled(false);
    }

    public void Toggle()
    {
        SetEnabled(!Enabled);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
            return;

        if (enabled && _playerMan.LocalEntity is null)
            return;

        Enabled = enabled;

        ResolveControls();

        if (_mainViewport != null)
            _mainViewport.Visible = !Enabled;

        if (_viewport != null)
        {
            _viewport.Visible = Enabled;

            if (Enabled)
            {
                // Start looking the way the movement frame already points, so entering the mode
                // doesn't immediately provoke a rotation.
                _viewport.Camera.Yaw = 0;
                _viewport.Camera.PitchPixels = 0;
            }
        }

        // Any direction we are holding on the player's behalf is ours to release. Leaving one
        // pressed would have them walking away on their own after returning to top-down.
        if (!Enabled)
            ReleaseAll();

        // Leaving the mode with the cursor key still held would strand the flag on, so the next
        // entry would start with no mouse-look and no obvious reason why.
        if (!Enabled)
            CursorFreed = false;

        SetMouseCapture(Enabled && !CursorFreed);
    }

    /// <summary>
    /// Whether the player is holding the cursor key, which releases mouse-look so the cursor can
    /// reach the HUD.
    /// </summary>
    /// <remarks>
    /// Captured mouse-look makes the rest of the game unusable on its own: with the cursor hidden and
    /// confined there is no pointer to open a context menu with, click a hotbar slot, or pick an
    /// option out of a verb flyout. Hold-to-free is the standard answer, and it is a hold rather than
    /// a toggle so there is never a question of which mode you are in.
    ///
    /// <see cref="FirstPersonViewport"/> reads this to stop turning the camera while it is set —
    /// without that, moving the pointer to a HUD button would spin the view with it and you would
    /// return facing somewhere else entirely.
    /// </remarks>
    public bool CursorFreed { get; private set; }

    private void SetCursorFreed(bool freed)
    {
        if (CursorFreed == freed)
            return;

        CursorFreed = freed;

        if (Enabled)
            SetMouseCapture(!freed);
    }

    /// <summary>
    /// Releases every move command this system currently has pressed.
    /// </summary>
    private void ReleaseAll()
    {
        for (var i = 0; i < _emitted.Length; i++)
        {
            if (_emitted[i])
                Emit(i, false);
        }

        _heldKeys = MoveKeys.None;
    }

    private void ResolveControls()
    {
        _mainViewport ??= _uiMan.ActiveScreen?.GetWidget<MainViewport>();
        _viewport ??= _uiMan.ActiveScreen?.GetWidget<FirstPersonViewport>();
    }

    private void SetMouseCapture(bool capture)
    {
        if (!_cfg.GetCVar(FirstPersonCVars.MouseCapture))
            return;

        _viewport?.Window?.SetRelativeMouseMode(capture);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (!Enabled)
            return;

        if (_playerMan.LocalEntity is not { } player || !EntityManager.EntityExists(player))
        {
            SetEnabled(false);
            return;
        }

        if (_viewport == null)
            return;

        _viewport.Camera.Position = _xform.GetWorldPosition(player);

        UpdateMovement(player);
    }

    /// <summary>
    /// Translates the camera-relative keys the player is holding into the grid direction they
    /// actually meant, and presses that direction on their behalf.
    /// </summary>
    /// <remarks>
    /// The movement frame itself is left alone. Rotating it (the obvious approach) can only move in
    /// 90° steps via <see cref="SharedMoverController.RotateCamera"/>, which the mover then glides
    /// toward over <see cref="InputMoverComponent.LerpTime"/> — so "forward" lags the camera by up
    /// to 45° and then slides, which is what made the mode feel broken. Remapping at the input layer
    /// has none of that: it is exact every frame, needs no new netcode, and leaves top-down alone.
    ///
    /// Resolution is limited to the eight grid directions because that is all SS14 movement has —
    /// <c>MoveButtons</c> is four bits. This quantises once, at the last possible moment.
    /// </remarks>
    private void UpdateMovement(EntityUid player)
    {
        // Turned off mid-hold: hand the keys back rather than leaving a direction pressed.
        if (!CameraRelativeMovement)
        {
            ReleaseAll();
            return;
        }

        Array.Clear(_want);

        if (TryGetGridDirection(player, out var dir))
            DirectionToButtons(dir, _want);

        for (var i = 0; i < _want.Length; i++)
        {
            if (_want[i] != _emitted[i])
                Emit(i, _want[i]);
        }
    }

    /// <summary>
    /// The grid-space direction the held keys add up to, or false if they cancel out.
    /// </summary>
    private bool TryGetGridDirection(EntityUid player, out Direction dir)
    {
        dir = default;

        if (_heldKeys == MoveKeys.None || _viewport == null)
            return false;

        if (!TryComp<InputMoverComponent>(player, out var mover))
            return false;

        // The mover rotates the button vector by GetParentGridAngle before applying it, so to end
        // up pointing along the camera we hand it the camera yaw with that angle taken back out.
        // Using the mover's own accessor covers grid rotation and any camera rotation the player
        // applied in top-down, rather than assuming both are zero.
        var forward = _viewport.Camera.Yaw - _mover.GetParentGridAngle(mover);

        var sum = Vector2.Zero;

        if ((_heldKeys & MoveKeys.Forward) != 0)
            sum += forward.ToVec();

        if ((_heldKeys & MoveKeys.Back) != 0)
            sum -= forward.ToVec();

        // Strafe basis: forward turned a quarter turn counter-clockwise, which is left in the
        // +X east / +Y north frame the mover works in.
        var strafe = (forward + Math.PI / 2).ToVec();

        if ((_heldKeys & MoveKeys.Left) != 0)
            sum += strafe;

        if ((_heldKeys & MoveKeys.Right) != 0)
            sum -= strafe;

        // Opposing keys cancelled exactly.
        if (sum.LengthSquared() < 0.001f)
            return false;

        // Must be FromWorldVec, not new Angle(sum). GetDir and GetCardinalDir put theta 0 at South
        // to match ToWorldVec, while new Angle(vec) and ToVec put it at East. Feeding one convention
        // to the other rotated every direction a quarter turn, so W strafed instead of walking
        // forward. FromWorldVec is exactly that quarter turn.
        var angle = Angle.FromWorldVec(sum);

        dir = _mover.DiagonalMovementEnabled ? angle.GetDir() : angle.GetCardinalDir();
        return true;
    }

    private static void DirectionToButtons(Direction dir, bool[] want)
    {
        switch (dir)
        {
            case Direction.North:
                want[North] = true;
                break;
            case Direction.NorthEast:
                want[North] = true;
                want[East] = true;
                break;
            case Direction.East:
                want[East] = true;
                break;
            case Direction.SouthEast:
                want[South] = true;
                want[East] = true;
                break;
            case Direction.South:
                want[South] = true;
                break;
            case Direction.SouthWest:
                want[South] = true;
                want[West] = true;
                break;
            case Direction.West:
                want[West] = true;
                break;
            case Direction.NorthWest:
                want[North] = true;
                want[West] = true;
                break;
        }
    }

    /// <summary>
    /// Presses or releases one of the four move functions through the normal predicted input
    /// pipeline, so client and server agree on it exactly as if the player had pressed the key.
    /// </summary>
    private void Emit(int index, bool down)
    {
        _emitted[index] = down;

        if (_playerMan.LocalSession is not { } session || _playerMan.LocalEntity is not { } player)
            return;

        var func = MoveFunctions[index];
        var funcId = _inputMan.NetworkBindMap.KeyFunctionID(func);

        var message = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, funcId)
        {
            State = down ? BoundKeyState.Down : BoundKeyState.Up,
            Coordinates = Transform(player).Coordinates,
        };

        // replay: true skips InputSystem's held-state bookkeeping. Without it the physical key the
        // player pressed and the direction we substitute would collide in that table — pressing W
        // already marked MoveUp down, so our own MoveUp would be swallowed as a duplicate.
        _emitting = true;

        try
        {
            _inputSystem.HandleInputCommand(session, func, message, replay: true);
        }
        finally
        {
            _emitting = false;
        }
    }

    private enum MoveKeys : byte
    {
        None = 0,
        Forward = 1 << 0,
        Back = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
    }

    /// <summary>
    /// Records a camera-relative key press and swallows it, so the mover never sees the raw WASD
    /// direction while first-person is active. <see cref="UpdateMovement"/> presses the real
    /// direction instead.
    /// </summary>
    private sealed class MoveKeyHandler : InputCmdHandler
    {
        private readonly FirstPersonSystem _system;
        private readonly MoveKeys _key;

        public MoveKeyHandler(FirstPersonSystem system, MoveKeys key)
        {
            _system = system;
            _key = key;
        }

        public override bool HandleCmdMessage(IEntityManager entManager, ICommonSession? session, IFullInputCmdMessage message)
        {
            // Top-down must behave exactly as upstream does.
            if (!_system.Enabled || !_system.CameraRelativeMovement)
                return false;

            // One of our own substituted commands — either as Emit sends it, or as prediction
            // replays it from the dispatch buffer each tick. Both have to reach the mover.
            // Intercepting the replay made movement apply for a single frame and then get rolled
            // back on every prediction pass, which is what the flickering was.
            if (_system._emitting || _system._inputSystem.Predicted)
                return false;

            if (message.State == BoundKeyState.Down)
            {
                _system._heldKeys |= _key;

                // Swallowed: UpdateMovement presses the direction the player actually meant.
                // Blocks the mover and the network send both, per InputSystem.HandleInputCommand.
                return true;
            }

            _system._heldKeys &= ~_key;

            // Releases always go through. If this key went down before first-person was toggled on,
            // the mover is still holding the raw direction and this is the only thing that will
            // ever clear it — swallowing it left the player walking off in that direction forever.
            return false;
        }
    }
}
