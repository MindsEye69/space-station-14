using System.Numerics;
using Content.Client.FirstPerson.Camera;
using Content.Client.FirstPerson.Render;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.Client.FirstPerson;

/// <summary>
/// Draws the first-person view. Sits alongside the 2D MainViewport and takes over the same rect
/// while first-person mode is active.
/// </summary>
/// <remarks>
/// Renders at a fixed low internal resolution into a render target, then blits it upscaled with
/// nearest-neighbour. This caps per-frame rasterisation cost regardless of window size and matches
/// the game's pixel-art look.
/// </remarks>
public sealed class FirstPersonViewport : UIWidget, IViewportControl
{
    /// <summary>How far the crosshair reaches, in tiles, when nothing blocks it sooner.</summary>
    private const float PickRange = 16f;

    /// <summary>Step size while marching the pick ray, in tiles.</summary>
    private const float PickStep = 0.2f;

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IPlayerManager _playerMan = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IResourceCache _resCache = default!;
    [Dependency] private IInputManager _inputMan = default!;
    [Dependency] private ISurfacePalette _palette = default!;

    private IRenderTexture? _target;
    private Vector2i _targetSize;
    private FirstPersonRenderer? _renderer;
    private Texture? _wallTexture;

    private readonly Action _renderAction;
    private DrawingHandleScreen? _drawHandle;
    private EntityUid _drawPlayer;
    private Vector2i _drawSize;
    private int _drawMaxRange;
    private bool _drawEntities;
    private bool _drawHalfHeight;
    private float _halfHeight;

    public readonly FirstPersonCamera Camera = new();

    public FirstPersonViewport()
    {
        IoCManager.InjectDependencies(this);
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
        _renderAction = RenderToTarget;
    }

    /// <summary>
    /// Hands clicks to the game as coming from *this* viewport.
    /// </summary>
    /// <remarks>
    /// Implementing <see cref="IViewportControl"/> is necessary but not sufficient. InputManager
    /// calls <c>ViewportKeyEvent(null, ...)</c> when the UI declines a bind, and
    /// <c>GameplayStateBase.OnKeyBindStateChanged</c> only resolves a target when the viewport is
    /// non-null and implements the interface. A control has to nominate itself, exactly as
    /// <c>ScalingViewport</c> does. Without this every interaction still arrived with
    /// <see cref="EntityCoordinates.Invalid"/> and no target.
    /// </remarks>
    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (args.Handled)
            return;

        _inputMan.ViewportKeyEvent(this, args);
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (args.Handled)
            return;

        _inputMan.ViewportKeyEvent(this, args);
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);

        if (!Visible)
            return;

        var sensitivity = _cfg.GetCVar(FirstPersonCVars.MouseSensitivity);

        // Subtracted, not added. WallPass fans its rays from Direction - Plane on the left edge to
        // Direction + Plane on the right, and Plane is the forward vector turned clockwise — so
        // screen-right is a *lower* yaw. Adding here turned the view the opposite way to the mouse,
        // which also mirrored the world relative to the movement maths and inverted strafing.
        Camera.Yaw -= Angle.FromDegrees(args.Relative.X * sensitivity);
        Camera.PitchPixels -= args.Relative.Y * sensitivity * 4f;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        if (_playerMan.LocalEntity is not { } player || !_entMan.EntityExists(player))
            return;

        var size = new Vector2i(_cfg.GetCVar(FirstPersonCVars.RenderWidth), _cfg.GetCVar(FirstPersonCVars.RenderHeight));
        EnsureTarget(size);

        Camera.FovDegrees = _cfg.GetCVar(FirstPersonCVars.Fov);
        Camera.Height = _cfg.GetCVar(FirstPersonCVars.EyeHeight);
        Camera.ClampPitch(size.Y);

        _renderer ??= new FirstPersonRenderer(_entMan, _palette);
        _wallTexture ??= ResolveWallTexture();

        // Stashed in fields so the render callback can stay a cached delegate. A lambda capturing
        // these would allocate a closure every frame.
        _drawPlayer = player;
        _drawHandle = handle;
        _drawSize = size;
        _drawMaxRange = _cfg.GetCVar(FirstPersonCVars.MaxRange);
        _drawEntities = _cfg.GetCVar(FirstPersonCVars.DrawEntities);
        _drawHalfHeight = _cfg.GetCVar(FirstPersonCVars.DrawHalfHeight);
        _halfHeight = _cfg.GetCVar(FirstPersonCVars.HalfHeight);

        handle.RenderInRenderTarget(_target!, _renderAction, Color.Black);

        _drawHandle = null;

        handle.DrawTextureRect(_target!.Texture, UIBox2.FromDimensions(Vector2.Zero, PixelSize));
    }

    private void RenderToTarget()
    {
        if (_drawHandle is not { } handle || _renderer is null || _wallTexture is null)
            return;

        _renderer.Render(
            handle,
            Camera,
            _drawPlayer,
            _drawSize.X,
            _drawSize.Y,
            _drawMaxRange,
            _wallTexture,
            _drawEntities,
            _drawHalfHeight,
            _halfHeight);
    }

    private void EnsureTarget(Vector2i size)
    {
        if (_target != null && _targetSize == size)
            return;

        _target?.Dispose();
        _target = _clyde.CreateRenderTarget(
            size,
            new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb),
            new TextureSampleParameters { Filter = false },
            nameof(FirstPersonViewport));
        _targetSize = size;
    }

    /// <summary>
    /// M1 renders walls as flat shaded colour (spec §6, option 2) rather than sampling a sprite.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the wall RSI. Every RSI in the game is packed into one shared atlas, and
    /// feeding that atlas' source texture through this pass corrupted top-down rendering — sprites
    /// and HUD alike turned black. Top-down parity is non-negotiable, so walls use the stock white
    /// texture and get their appearance from per-vertex colour instead. SS14 has no side-elevation
    /// wall art anyway, so a sampled top-down icon was never going to be right.
    /// </remarks>
    private static Texture ResolveWallTexture()
    {
        return Texture.White;
    }

    /// <summary>
    /// Resolves a screen point to the map point the player is aiming at.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the widget implements <see cref="IViewportControl"/>.
    /// <c>GameplayStateBase.OnKeyBindStateChanged</c> is the single funnel every world interaction
    /// passes through — use, alt-use, examine, drag-drop, context menus — and it only fills in the
    /// command's coordinates and target when the viewport implements this interface. Without it
    /// every interaction was dispatched with <see cref="EntityCoordinates.Invalid"/> and no target,
    /// which is why first person could walk but not touch anything.
    ///
    /// The ray is marched rather than simply run to the wall, because stopping at the wall would
    /// mean aiming straight through the person standing in front of it.
    /// </remarks>
    public MapCoordinates PixelToMap(Vector2 point)
    {
        if (_playerMan.LocalEntity is not { } player || !_entMan.EntityExists(player))
            return MapCoordinates.Nullspace;

        var mapId = _entMan.GetComponent<TransformComponent>(player).MapID;
        var size = _targetSize == Vector2i.Zero ? new Vector2i(640, 360) : _targetSize;

        // With the cursor captured its OS position is meaningless, so aim dead centre: a real
        // crosshair. With capture off the cursor is visible and free-aim is what the player expects.
        var local = _cfg.GetCVar(FirstPersonCVars.MouseCapture)
            ? new Vector2(size.X / 2f, size.Y / 2f)
            : (point - GlobalPixelPosition) / Vector2.Max(PixelSize, Vector2.One) * size;

        var column = (int) Math.Clamp(local.X, 0, size.X - 1);
        var dir = Vector2.Normalize(Camera.RayDirection(column, size.X));

        // Never reach past the wall the crosshair is looking at.
        var limit = MathF.Min(_renderer?.GetWallDepth(column) ?? PickRange, PickRange);

        var lookup = _entMan.System<EntityLookupSystem>();
        var origin = Camera.Position;

        for (var travelled = PickStep; travelled <= limit; travelled += PickStep)
        {
            var probe = new MapCoordinates(origin + dir * travelled, mapId);

            // Excluding Contained is essential. The default flags include it, so the player's own
            // worn clothing and carried items — which sit at the player's own position — are found
            // on the very first step and every click targets your own shoes.
            foreach (var found in lookup.GetEntitiesInRange(probe, PickStep, LookupFlags.All & ~LookupFlags.Contained))
            {
                if (found == player)
                    continue;

                return probe;
            }
        }

        // Nothing in the way: hand back the far end so the player can still click bare floor.
        return new MapCoordinates(origin + dir * limit, mapId);
    }

    public MapCoordinates ScreenToMap(Vector2 coords) => PixelToMap(coords);

    /// <summary>
    /// Projects a world point back to the screen, for callers that want to place UI over an entity.
    /// </summary>
    public Vector2 WorldToScreen(Vector2 map)
    {
        var size = _targetSize == Vector2i.Zero ? new Vector2i(640, 360) : _targetSize;
        var cam = Camera.WorldToCamera(map);

        // Behind the camera. There is no sensible screen position, so park it off-screen rather
        // than returning a mirrored one that a caller would happily draw.
        if (cam.Y <= 0.01f)
            return new Vector2(float.MinValue, float.MinValue);

        var local = new Vector2(
            size.X / 2f * (1f + cam.X / cam.Y),
            size.Y / 2f + Camera.PitchPixels + size.Y / cam.Y * (Camera.Height - 0.5f));

        return GlobalPixelPosition + local / size * Vector2.Max(PixelSize, Vector2.One);
    }

    /// <summary>
    /// Not representable. A perspective projection is not affine, so it cannot be expressed as a
    /// <see cref="Matrix3x2"/> — use <see cref="WorldToScreen"/> instead. Identity is returned so
    /// that callers reaching for this get something inert rather than NaNs.
    /// </summary>
    public Matrix3x2 GetWorldToScreenMatrix() => Matrix3x2.Identity;

    /// <summary>
    /// Control-local to screen, which genuinely is affine.
    /// </summary>
    public Matrix3x2 GetLocalToScreenMatrix()
    {
        var pos = GlobalPixelPosition;
        return new Matrix3x2(1f, 0f, 0f, 1f, pos.X, pos.Y);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        _target?.Dispose();
        _target = null;
    }
}
