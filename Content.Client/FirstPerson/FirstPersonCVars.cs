using Robust.Shared.Configuration;

namespace Content.Client.FirstPerson;

[CVarDefs]
public sealed class FirstPersonCVars
{
    /// <summary>
    /// Internal horizontal render resolution. The view is rendered at this size and blitted upscaled.
    /// </summary>
    public static readonly CVarDef<int> RenderWidth =
        CVarDef.Create("firstperson.render_width", 640, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<int> RenderHeight =
        CVarDef.Create("firstperson.render_height", 360, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Horizontal field of view, degrees.
    /// </summary>
    public static readonly CVarDef<float> Fov =
        CVarDef.Create("firstperson.fov", 70f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Maximum DDA raycast distance, in tiles.
    /// </summary>
    public static readonly CVarDef<int> MaxRange =
        CVarDef.Create("firstperson.max_range", 32, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Eye height above the floor plane, in tiles (1 tile = 1m).
    /// </summary>
    public static readonly CVarDef<float> EyeHeight =
        CVarDef.Create("firstperson.eye_height", 0.55f, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<float> MouseSensitivity =
        CVarDef.Create("firstperson.mouse_sensitivity", 0.3f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Whether entering first person captures the cursor. When false, the cursor stays visible and
    /// yaw follows its offset from the viewport centre instead.
    /// </summary>
    public static readonly CVarDef<bool> MouseCapture =
        CVarDef.Create("firstperson.mouse_capture", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Draw the entity billboard pass. Turn off to inspect walls and floors in isolation.
    /// </summary>
    public static readonly CVarDef<bool> DrawEntities =
        CVarDef.Create("firstperson.draw_entities", true, CVar.CLIENTONLY);

    /// <summary>
    /// Draw waist-height structures (counters, tables, half walls) as short geometry.
    /// Turn off to see only full-height walls, which isolates which pass is responsible for
    /// unexpected geometry.
    /// </summary>
    public static readonly CVarDef<bool> DrawHalfHeight =
        CVarDef.Create("firstperson.draw_half_height", true, CVar.CLIENTONLY);

    /// <summary>
    /// Height of waist-height structures, in tiles. Must stay below <see cref="EyeHeight"/>.
    /// </summary>
    /// <remarks>
    /// The gap between this and the eye is the only thing that makes the top of a counter visible:
    /// the cap covers <c>(eye - half) * viewHeight / distance</c> pixels, so at the old 0.5 it was a
    /// 2px sliver at ordinary room range and a counter read as a featureless slab. At 0.32 the same
    /// counter measures 10px of top against 60px of face, which is the difference between a box and
    /// a table.
    ///
    /// 0.32 is also about physically right rather than merely legible. Taking the 0.55 eye as a
    /// standing adult's 1.6m puts a tile at roughly 2.9m, which makes a real 0.9m counter 0.31.
    ///
    /// Going above <see cref="EyeHeight"/> is not merely odd-looking: you would be under the surface
    /// rather than over it, and <c>WallPass.AppendCaps</c> depends on being above it to guarantee
    /// caps and faces never overlap on screen.
    /// </remarks>
    public static readonly CVarDef<float> HalfHeight =
        CVarDef.Create("firstperson.half_height", 0.32f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Whether WASD is interpreted relative to the camera (spec §7.3 option A) rather than the grid.
    /// </summary>
    /// <remarks>
    /// On, W walks the way you are looking, quantised to the eight grid directions SS14 movement
    /// supports. Off restores vanilla grid-relative WASD, where W is always grid-north no matter
    /// where the camera points.
    /// </remarks>
    public static readonly CVarDef<bool> CameraRelativeMovement =
        CVarDef.Create("firstperson.camera_relative_movement", true, CVar.CLIENTONLY | CVar.ARCHIVE);
}
