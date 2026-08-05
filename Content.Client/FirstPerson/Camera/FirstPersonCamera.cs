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

    public void ClampPitch(float viewHeight)
    {
        var limit = viewHeight * 0.25f;
        PitchPixels = Math.Clamp(PitchPixels, -limit, limit);
    }
}
