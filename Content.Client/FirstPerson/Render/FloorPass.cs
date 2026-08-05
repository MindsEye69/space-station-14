using System.Numerics;
using Content.Client.FirstPerson.Camera;
using Robust.Client.Graphics;

namespace Content.Client.FirstPerson.Render;

/// <summary>
/// Floor and ceiling fill.
/// </summary>
/// <remarks>
/// M1 draws two flat half-screen bands rather than casting per-scanline floor spans. This is
/// deliberately a placeholder: it establishes the horizon and makes walls readable. Real floor
/// casting (per-scanline row distance, sampling the tile texture) is M2.
/// </remarks>
public sealed class FloorPass
{
    private static readonly Color CeilingColor = Color.FromHex("#2e3138");
    private static readonly Color FloorColor = Color.FromHex("#5a5f6b");

    public void Render(DrawingHandleScreen handle, FirstPersonCamera camera, int width, int height)
    {
        var horizon = height / 2f + camera.PitchPixels;

        handle.DrawRect(new UIBox2(0, 0, width, horizon), CeilingColor);
        handle.DrawRect(new UIBox2(0, horizon, width, height), FloorColor);
    }
}
