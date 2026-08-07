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

    /// <summary>
    /// The top surface of a counter. Lighter than the side, standing in for the fact that a
    /// horizontal face catches more light than a vertical one.
    /// </summary>
    /// <remarks>
    /// A flat tint on purpose, for now. This is the one face SS14 genuinely has art for — the
    /// top-down sprite is a picture of it taken from exactly this angle — so it is where a derived
    /// per-entity colour, and eventually the sprite itself, belongs.
    /// </remarks>
    private static readonly Color HalfTop = Color.FromHex("#b39a7c");

    /// <summary>Half-height hits kept per column. Beyond this, further ones are dropped.</summary>
    private const int MaxHalfHits = 4;

    /// <summary>Tiles capped per frame. Beyond this the most distant ones are dropped.</summary>
    private const int MaxCapTiles = 256;

    /// <summary>
    /// Height of a waist-height structure, in tiles. Set from <c>firstperson.half_height</c>; kept
    /// below the eye height on purpose, so the player looks down at counters rather than level with
    /// them. <see cref="EntityPass"/> reads it to lift whatever is resting on top.
    /// </summary>
    public float HalfHeight = 0.32f;

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

    /// <summary>
    /// Distinct waist-height tiles crossed this frame, gathered across every column so each is
    /// capped once rather than once per column that saw it.
    /// </summary>
    private readonly HashSet<Vector2i> _capTiles = new();
    private readonly List<Cap> _caps = new();

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

        EnsureVertCapacity(width * 6 * (MaxHalfHits + 1) + MaxCapTiles * 6);
        _vertCount = 0;
        _capTiles.Clear();

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

        if (DrawHalfHeight)
            AppendCaps(camera, width, height, horizon);

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
            //
            // The tile is noted before the per-column cap is applied. Dropping the fifth hit in a
            // column only costs that column a face, but dropping its tile would punch a hole in a
            // surface every other column can see.
            _capTiles.Add(new Vector2i(mapX, mapY));

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

    /// <summary>
    /// Lays a horizontal quad over the top of every waist-height tile crossed this frame.
    /// </summary>
    /// <remarks>
    /// Without this a counter is a vertical face and nothing else — a short wall with no lid, which
    /// is most of why furniture used to read as architecture.
    ///
    /// Caps are whole tiles rather than per-column strips, unlike the faces. Ordering makes that
    /// safe. Everything projects as 1/depth, so a face at distance d covers
    /// <c>[horizon + (H-h)/d, horizon + H/d]</c> and a cap covers <c>[horizon + (H-h)/d_far,
    /// horizon + (H-h)/d_near]</c>: a tile's cap ends exactly where its own face begins, and any
    /// farther cap sits entirely above any nearer face. No cap and no face ever overlap, so their
    /// draw order is free. Caps are also coplanar with each other, so like floor tiles they project
    /// disjointly and their mutual order is free too — which is what lets this be one flat batch
    /// appended after the columns instead of a merged depth sort.
    ///
    /// That argument needs the eye to be above the surface. Set <c>firstperson.eye_height</c> below
    /// <c>firstperson.half_height</c> and you are looking at the underside: caps and faces begin to
    /// overlap, and the painter's algorithm here stops being sufficient.
    /// </remarks>
    private void AppendCaps(FirstPersonCamera camera, int width, int height, float horizon)
    {
        if (_capTiles.Count == 0)
            return;

        _caps.Clear();

        foreach (var tile in _capTiles)
        {
            var centre = new Vector2(tile.X + 0.5f, tile.Y + 0.5f);
            _caps.Add(new Cap(tile, camera.WorldToCamera(centre).Y));
        }

        // Nearest first. Order is free, so distance drives it purely for the vertex budget: when the
        // buffer fills, the caps given up are the distant ones rather than whichever sorted last.
        _caps.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        foreach (var cap in _caps)
        {
            var t = cap.Tile;

            if (!camera.ProjectSurface(new Vector2(t.X, t.Y), width, height, horizon, HalfHeight, out var a) ||
                !camera.ProjectSurface(new Vector2(t.X + 1, t.Y), width, height, horizon, HalfHeight, out var b) ||
                !camera.ProjectSurface(new Vector2(t.X + 1, t.Y + 1), width, height, horizon, HalfHeight, out var c) ||
                !camera.ProjectSurface(new Vector2(t.X, t.Y + 1), width, height, horizon, HalfHeight, out var d))
            {
                continue;
            }

            // Entirely off one side. Without this a tile beside the camera projects to an extreme
            // screen X and smears a long wedge across the view.
            if ((a.X < 0 && b.X < 0 && c.X < 0 && d.X < 0) ||
                (a.X > width && b.X > width && c.X > width && d.X > width))
            {
                continue;
            }

            // Occlusion against full-height walls, sampled at the tile centre rather than per pixel.
            var centreX = (int) ((a.X + c.X) / 2f);
            if (centreX >= 0 && centreX < Depth.Length && Depth[centreX] < cap.Distance)
                continue;

            if (_vertCount + 6 > _verts.Length)
                break;

            var shade = Math.Max(1f / (1f + cap.Distance * 0.04f), 0.45f);

            var color = Color.FromSrgb(new Color(
                HalfTop.R * shade,
                HalfTop.G * shade,
                HalfTop.B * shade,
                1f));

            AppendVert(a, color);
            AppendVert(b, color);
            AppendVert(c, color);
            AppendVert(a, color);
            AppendVert(c, color);
            AppendVert(d, color);
        }
    }

    private void AppendVert(Vector2 position, Color color)
    {
        _verts[_vertCount++] = new DrawVertexUV2DColor(position, Vector2.Zero, color);
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

    /// <summary>A waist-height tile and the perpendicular distance to its centre.</summary>
    private readonly record struct Cap(Vector2i Tile, float Distance);
}
