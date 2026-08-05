using System.Numerics;
using Content.Client.FirstPerson.Camera;
using Content.Client.FirstPerson.Render;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Graphics;
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
public sealed class FirstPersonViewport : UIWidget
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IPlayerManager _playerMan = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IResourceCache _resCache = default!;

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

        _renderer ??= new FirstPersonRenderer(_entMan);
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        _target?.Dispose();
        _target = null;
    }
}
