using System.Numerics;
using Content.Client.FirstPerson.Camera;
using Robust.Client.Graphics;
using Robust.Shared.Map.Components;

namespace Content.Client.FirstPerson.Render;

/// <summary>
/// Per-column DDA raycast against the grid, emitting textured wall strips.
/// </summary>
/// <remarks>
/// Walls are drawn as 1px-wide column quads rather than whole-surface quads: affine texture mapping
/// on a large two-triangle quad warps visibly, and per-column strips batch cheaply anyway.
///
/// The cast records two kinds of hit. A full-height tile ends the ray, as in Wolfenstein. A
/// waist-height tile — a counter, table or machine — does not: you can see over it, so it is
/// recorded as a short quad and the ray continues past. That is what stops furniture from having to
/// be a billboard, which matters because SS14's furniture art is drawn top-down and can never look
/// right pasted onto a camera-facing card.
/// </remarks>
public sealed class WallPass
{
    /// <summary>Base wall colour, roughly the station's steel plating.</summary>
    private static readonly Color WallBase = Color.FromHex("#9aa0ad");

    /// <summary>Warmer than the walls, so furniture reads as furniture at a glance.</summary>
    private static readonly Color HalfBase = Color.FromHex("#9b8467");

    /// <summary>Half-height hits kept per column. Beyond this, further ones are dropped.</summary>
    private const int MaxHalfHits = 4;

    /// <summary>
    /// Height of a waist-height structure, in tiles. Set from <c>firstperson.half_height</c>; kept
    /// below the eye height on purpose, so the player looks down at counters rather than level with
    /// them. <see cref="EntityPass"/> reads it to lift whatever is resting on top.
    /// </summary>
    public float HalfHeight = 0.5f;

    /// <summary>Whether waist-height structures are drawn at all.</summary>
    public bool DrawHalfHeight = true;

    private readonly TileSolidityCache _solidity;

    /// <summary>
    /// Per-column perpendicular distance to the nearest full-height wall, or
    /// <see cref="float.MaxValue"/>. Half-height hits deliberately do not appear here — they must
    /// not cull the entities standing behind them, because you can see over them.
    /// </summary>
    public float[] Depth = new float[1];

    private DrawVertexUV2DColor[] _verts = new DrawVertexUV2DColor[6 * 64];
    private int _vertCount;

    private readonly Hit[] _halfHits = new Hit[MaxHalfHits];

    public WallPass(TileSolidityCache solidity)
    {
        _solidity = solidity;
    }

    public void Render(
        DrawingHandleScreen handle,
        FirstPersonCamera camera,
        Entity<MapGridComponent> grid,
        int width,
        int height,
        int maxRange,
        Texture wallTexture)
    {
        if (Depth.Length != width)
            Depth = new float[width];

        // NB: wallTexture must be a plain texture, not an RSI frame. RSI frames are regions of the
        // one shared sprite atlas, and routing that atlas through this batch corrupted top-down
        // rendering. Appearance comes from per-vertex colour instead.

        EnsureVertCapacity(width * 6 * (MaxHalfHits + 1));
        _vertCount = 0;

        var horizon = height / 2f + camera.PitchPixels;

        for (var x = 0; x < width; x++)
        {
            Depth[x] = float.MaxValue;

            var rayDir = camera.RayDirection(x, width);
            var halfCount = Cast(camera.Position, rayDir, grid, maxRange, DrawHalfHeight, out var full);

            // Painter's algorithm, far to near — there is no depth buffer. The full wall ends the
            // ray so it sits behind every half hit, and the DDA yields half hits nearest-last.
            if (full.Valid)
            {
                Depth[x] = full.Distance;
                AppendColumn(x, horizon, height, camera.Height, full, 1f, WallBase);
            }

            for (var i = halfCount - 1; i >= 0; i--)
            {
                AppendColumn(x, horizon, height, camera.Height, _halfHits[i], HalfHeight, HalfBase);
            }
        }

        if (_vertCount > 0)
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, wallTexture, _verts.AsSpan(0, _vertCount));
    }

    /// <summary>
    /// Standard DDA. Distances are perpendicular, not euclidean — euclidean produces fisheye.
    /// Fills <see cref="_halfHits"/> with the waist-height tiles crossed, nearest first, and returns
    /// how many there were. <paramref name="full"/> is the full-height tile that stopped the ray,
    /// if any.
    /// </summary>
    private int Cast(
        Vector2 origin,
        Vector2 rayDir,
        Entity<MapGridComponent> grid,
        int maxRange,
        bool recordHalf,
        out Hit full)
    {
        full = default;
        var halfCount = 0;

        var mapX = (int) MathF.Floor(origin.X);
        var mapY = (int) MathF.Floor(origin.Y);

        var deltaDistX = rayDir.X == 0f ? float.MaxValue : MathF.Abs(1f / rayDir.X);
        var deltaDistY = rayDir.Y == 0f ? float.MaxValue : MathF.Abs(1f / rayDir.Y);

        int stepX, stepY;
        float sideDistX, sideDistY;

        if (rayDir.X < 0)
        {
            stepX = -1;
            sideDistX = (origin.X - mapX) * deltaDistX;
        }
        else
        {
            stepX = 1;
            sideDistX = (mapX + 1f - origin.X) * deltaDistX;
        }

        if (rayDir.Y < 0)
        {
            stepY = -1;
            sideDistY = (origin.Y - mapY) * deltaDistY;
        }
        else
        {
            stepY = 1;
            sideDistY = (mapY + 1f - origin.Y) * deltaDistY;
        }

        var side = 0;

        for (var i = 0; i < maxRange * 2; i++)
        {
            if (sideDistX < sideDistY)
            {
                sideDistX += deltaDistX;
                mapX += stepX;
                side = 0;
            }
            else
            {
                sideDistY += deltaDistY;
                mapY += stepY;
                side = 1;
            }

            var dist = side == 0
                ? sideDistX - deltaDistX
                : sideDistY - deltaDistY;

            if (dist > maxRange)
                return halfCount;

            var solidity = _solidity.GetSolidity(grid, new Vector2i(mapX, mapY));

            if (solidity == TileSolidity.None || (solidity == TileSolidity.Half && !recordHalf))
                continue;

            var perpDist = MathF.Max(dist, 0.0001f);

            // Where along the face the ray landed, for the texture U coordinate.
            var wallX = side == 0
                ? origin.Y + perpDist * rayDir.Y
                : origin.X + perpDist * rayDir.X;
            wallX -= MathF.Floor(wallX);

            var hit = new Hit(perpDist, wallX, side);

            if (solidity == TileSolidity.Full)
            {
                full = hit;
                return halfCount;
            }

            // Waist height: record it and keep going, because the player can see over it.
            if (halfCount < _halfHits.Length)
                _halfHits[halfCount++] = hit;
        }

        return halfCount;
    }

    /// <summary>
    /// Emits one 1px column for a surface standing on the floor and rising
    /// <paramref name="worldHeight"/> tiles.
    /// </summary>
    private void AppendColumn(
        int x,
        float horizon,
        int viewHeight,
        float eyeHeight,
        in Hit hit,
        float worldHeight,
        Color baseColor)
    {
        // Pixels per tile at this distance. The floor plane sits eyeHeight tiles below the eye, so
        // it projects that far below the horizon; the top of the surface is worldHeight above that.
        var scale = viewHeight / hit.Distance;
        var bottom = horizon + scale * eyeHeight;
        var top = horizon + scale * (eyeHeight - worldHeight);

        // N/S faces are drawn darker than E/W so corners stay readable without real lighting.
        // Kept gentle and floored: this substitutes for lighting, and anything steeper makes
        // surfaces read as black at ordinary room distances.
        var shade = Math.Max(1f / (1f + hit.Distance * 0.04f), 0.45f);
        if (hit.Side == 1)
            shade *= 0.8f;

        var color = Color.FromSrgb(new Color(
            baseColor.R * shade,
            baseColor.G * shade,
            baseColor.B * shade,
            1f));

        var left = x;
        var right = x + 1;

        // A 1px slice of the texture. Nudged inward to avoid bleeding into the neighbouring column.
        var u0 = Math.Clamp(hit.WallX, 0.001f, 0.999f);

        var tl = new DrawVertexUV2DColor(new Vector2(left, top), new Vector2(u0, 0f), color);
        var tr = new DrawVertexUV2DColor(new Vector2(right, top), new Vector2(u0, 0f), color);
        var bl = new DrawVertexUV2DColor(new Vector2(left, bottom), new Vector2(u0, 1f), color);
        var br = new DrawVertexUV2DColor(new Vector2(right, bottom), new Vector2(u0, 1f), color);

        _verts[_vertCount++] = tl;
        _verts[_vertCount++] = tr;
        _verts[_vertCount++] = br;
        _verts[_vertCount++] = tl;
        _verts[_vertCount++] = br;
        _verts[_vertCount++] = bl;
    }

    private void EnsureVertCapacity(int needed)
    {
        if (_verts.Length < needed)
            _verts = new DrawVertexUV2DColor[needed];
    }

    private readonly record struct Hit(float Distance, float WallX, int Side)
    {
        public bool Valid => Distance > 0f;
    }
}
