using System.Numerics;

namespace Content.Client.FirstPerson.Camera;

/// <summary>
/// Camera state and projection for the first-person view.
/// </summary>
/// <remarks>
/// Pitch is a horizon shift in pixels rather than a real rotation (the Doom/Build approach). This
/// keeps walls vertical so the column rasteriser stays valid.
/// </remarks>
public sealed class FirstPersonCamera
{
    /// <summary>
    /// Closest a projected point may be before it is discarded, in tiles.
    /// </summary>
    /// <remarks>
    /// Half a tile rather than a hair off zero: everything here scales as 1/depth, so anything nearer
    /// explodes to hundreds of times its size and swallows the screen.
    /// </remarks>
    public const float NearPlane = 0.5f;

    public Vector2 Position;
    public Angle Yaw;
    public float PitchPixels;
    public float Height = 0.55f;
    public float FovDegrees = 70f;

    /// <summary>
    /// Forward unit vector.
    /// </summary>
    public Vector2 Direction => new((float) Math.Cos(Yaw), (float) Math.Sin(Yaw));

    /// <summary>
    /// Camera plane: perpendicular to <see cref="Direction"/>, length tan(fov/2). Sweeping this
    /// across [-1, 1] and adding it to the direction produces the per-column ray fan.
    /// </summary>
    public Vector2 Plane
    {
        get
        {
            var halfFov = MathF.Tan(float.DegreesToRadians(FovDegrees) / 2f);
            var dir = Direction;
            return new Vector2(dir.Y, -dir.X) * halfFov;
        }
    }

    /// <summary>
    /// Ray direction for screen column <paramref name="x"/> of a view <paramref name="width"/> wide.
    /// </summary>
    public Vector2 RayDirection(int x, int width)
    {
        var cameraX = 2f * x / width - 1f;
        return Direction + Plane * cameraX;
    }

    /// <summary>
    /// Transforms a world point into camera space, where +X is right along the camera plane and +Y
    /// is forward depth. Points with Y &lt;= 0 are behind the camera.
    /// </summary>
    public Vector2 WorldToCamera(Vector2 world)
    {
        var rel = world - Position;
        var dir = Direction;
        var plane = Plane;

        // Inverse of [plane dir], which maps camera space to world space.
        var invDet = 1f / (plane.X * dir.Y - dir.X * plane.Y);
        return new Vector2(
            invDet * (dir.Y * rel.X - dir.X * rel.Y),
            invDet * (-plane.Y * rel.X + plane.X * rel.Y));
    }

    /// <summary>
    /// Projects a point lying on a horizontal plane <paramref name="surfaceHeight"/> tiles above the
    /// floor into screen space. False if the point is nearer than <see cref="NearPlane"/>.
    /// </summary>
    /// <remarks>
    /// One routine covers the floor (height 0) and the top of a counter (height
    /// <c>firstperson.half_height</c>), because they differ only in how far below the eye the plane
    /// sits.
    ///
    /// Returning false so the caller discards the whole quad is deliberate, and clamping the depth
    /// instead is a trap that was tried and reverted. Screen Y goes as <c>height / depth</c>, so a
    /// corner just in front of the camera lands hundreds of pixels below the horizon while the far
    /// corners sit at ordinary positions — the quad becomes a long diagonal wedge sweeping across the
    /// view as you walk. Losing the tile underfoot, which is mostly out of frame anyway, is much
    /// cheaper than that.
    /// </remarks>
    public bool ProjectSurface(
        Vector2 gridPos,
        int width,
        int height,
        float horizon,
        float surfaceHeight,
        out Vector2 screen)
    {
        screen = default;

        var cam = WorldToCamera(gridPos);
        if (cam.Y <= NearPlane)
            return false;

        screen = new Vector2(
            width / 2f * (1f + cam.X / cam.Y),
            horizon + height / cam.Y * (Height - surfaceHeight));

        return true;
    }

    public void ClampPitch(float viewHeight)
    {
        var limit = viewHeight * 0.25f;
        PitchPixels = Math.Clamp(PitchPixels, -limit, limit);
    }
}
