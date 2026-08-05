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
    /// Height of waist-height structures, in tiles. Slightly below <see cref="EyeHeight"/> so the
    /// player looks down at counters rather than level with them.
    /// </summary>
    public static readonly CVarDef<float> HalfHeight =
        CVarDef.Create("firstperson.half_height", 0.5f, CVar.CLIENTONLY | CVar.ARCHIVE);

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
