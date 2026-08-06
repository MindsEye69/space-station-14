using System.Numerics;
using Content.Client.FirstPerson.Camera;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Client.FirstPerson.Render;

/// <summary>
/// Projects visible entities as directional billboards.
/// </summary>
/// <remarks>
/// SS14's RSI sprites already carry 4 or 8 cardinal directions per state, which is exactly the Doom
/// directional-billboard model — so <see cref="DrawingHandleScreen.DrawEntity"/> does the work once
/// given a screen position, a scale, and an override direction.
/// </remarks>
public sealed class EntityPass
{
    private const float PixelsPerMeter = 32f;

    /// <summary>Closest an entity may be before it is skipped, in tiles.</summary>
    private const float NearPlane = 0.5f;

    /// <summary>
    /// At or below this draw depth an entity lies flat on the floor plane rather than standing up.
    /// </summary>
    /// <remarks>
    /// Covers subfloor pipes and wires, carpets, catwalks and lattice, floor objects and puddles.
    /// <see cref="DrawDepth.HighFloorObjects"/> and above (levers, holopads, kudzu) keep standing,
    /// since those genuinely have height.
    /// </remarks>
    private const int FlatDepthCutoff = (int) Content.Shared.DrawDepth.DrawDepth.Puddles;

    private readonly EntityLookupSystem _lookup;
    private readonly SharedTransformSystem _xform;

    /// <summary>Walkable floor structures: catwalks, lattice, carpets.</summary>
    private static readonly Color WalkwayColor = Color.FromHex("#6e737f");

    /// <summary>Exposed pipes and wiring below the plating.</summary>
    private static readonly Color SubfloorColor = Color.FromHex("#4a4f5a");

    private readonly HashSet<Entity<SpriteComponent>> _candidates = new();
    private readonly List<Billboard> _billboards = new();
    private readonly List<FlatTile> _flats = new();

    private readonly DrawVertexUV2DColor[] _flatVerts = new DrawVertexUV2DColor[6 * 512];
    private int _vertCount;

    private EntityQuery<FixturesComponent> _fixtureQuery;

    public EntityPass(IEntityManager entMan, EntityLookupSystem lookup, SharedTransformSystem xform)
    {
        _lookup = lookup;
        _xform = xform;
        _fixtureQuery = entMan.GetEntityQuery<FixturesComponent>();
    }

    /// <summary>
    /// Whether WallPass already draws this entity as geometry.
    /// </summary>
    /// <remarks>
    /// Must stay the exact complement of the masks <see cref="TileSolidityCache"/> tests. If the two
    /// drift apart, a structure gets drawn twice — once as the raycast column and again as a
    /// camera-facing card of its top-down sprite pasted over it.
    ///
    /// Mobs are unaffected: <see cref="CollisionGroup.MobLayer"/> is Opaque | BulletImpassable, with
    /// no impassable bits of its own, so creatures stay billboards. That is the right split — their
    /// sprites are already drawn side-on with cardinal facings, which is exactly Doom's directional
    /// sprite model, whereas furniture art is drawn from above and only works as geometry.
    /// </remarks>
    private bool IsWallGeometry(EntityUid uid)
    {
        if (!_fixtureQuery.TryGetComponent(uid, out var fixtures))
            return false;

        const int mask = (int) (TileSolidityCache.FullMask | TileSolidityCache.HalfMask);

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (fixture.Hard && (fixture.CollisionLayer & mask) != 0)
                return true;
        }

        return false;
    }

    /// <param name="camera">Camera already expressed in grid-local space.</param>
    /// <param name="invGrid">World-to-grid-local matrix, to match entity positions to the camera.</param>
    /// <param name="gridRot">Grid world rotation, to convert entity facing into the camera's frame.</param>
    public void Render(
        DrawingHandleScreen handle,
        FirstPersonCamera camera,
        Entity<MapGridComponent> grid,
        TileSolidityCache solidity,
        Matrix3x2 invGrid,
        Angle gridRot,
        MapId mapId,
        EntityUid? excluded,
        float[] depth,
        int width,
        int height,
        int maxRange,
        float halfHeight)
    {
        _candidates.Clear();
        _billboards.Clear();
        _flats.Clear();

        // The lookup is world-space, so query around the camera's world position.
        Matrix3x2.Invert(invGrid, out var gridMatrix);
        var worldCameraPos = Vector2.Transform(camera.Position, gridMatrix);
        var bounds = Box2.CenteredAround(worldCameraPos, new Vector2(maxRange * 2f, maxRange * 2f));

        // Excluding Contained matters a lot: the default flags include it, which drags in the
        // player's own inventory and the contents of every nearby locker. Those sit at their
        // container's position, so they land at ~zero depth and get drawn screen-fillingly huge.
        _lookup.GetEntitiesIntersecting(mapId, bounds, _candidates, LookupFlags.All & ~LookupFlags.Contained);

        var horizon = height / 2f + camera.PitchPixels;

        foreach (var candidate in _candidates)
        {
            var uid = candidate.Owner;
            if (uid == excluded || !candidate.Comp.Visible)
                continue;

            // Anything WallPass already rasterises as geometry must not also be drawn as a
            // billboard, or every wall gets drawn twice: once as the correct raycast column, then
            // again as a camera-facing card of its top-down sprite painted over it.
            if (IsWallGeometry(uid))
                continue;

            // Floor-flat things — catwalks, lattice, carpets, puddles — are drawn from above in 2D.
            // Standing their sprite up as a camera-facing card turns a walkway you are standing on
            // into a wall of grating in front of your face. They cannot simply be dropped either:
            // over open space a catwalk IS the floor, and not drawing it means walking into space
            // and dying. So they get projected onto the floor plane as flat tiles instead.
            if (candidate.Comp.DrawDepth <= FlatDepthCutoff)
            {
                var flatPos = Vector2.Transform(_xform.GetWorldPosition(uid), invGrid);
                var flatTile = new Vector2i((int) MathF.Floor(flatPos.X), (int) MathF.Floor(flatPos.Y));
                var flatDepth = camera.WorldToCamera(new Vector2(flatTile.X + 0.5f, flatTile.Y + 0.5f)).Y;

                _flats.Add(new FlatTile(flatTile, candidate.Comp.DrawDepth, flatDepth));
                continue;
            }

            var (worldPos, worldRot) = _xform.GetWorldPositionRotation(uid);
            var gridPos = Vector2.Transform(worldPos, invGrid);
            var cam = camera.WorldToCamera(gridPos);

            // Behind or effectively on top of the camera. The near plane is half a tile rather than
            // a hair off zero: scale goes as 1/depth, so anything closer explodes to hundreds of
            // times its size and swallows the screen.
            if (cam.Y <= NearPlane)
                continue;

            var screenX = width / 2f * (1f + cam.X / cam.Y);
            if (screenX < 0 || screenX >= width)
                continue;

            // Occluded by a wall in its own column.
            var column = (int) screenX;
            if (column >= 0 && column < depth.Length && depth[column] < cam.Y)
                continue;

            // SS14 has no z-axis, so an item on a counter has the same coordinates as one on the
            // floor and would be drawn standing on the floor — visibly sunk into the counter that
            // WallPass now draws. The tile's own solidity is the missing height: if something
            // waist-high occupies it, whatever else is there is resting on top of it.
            var tile = new Vector2i((int) MathF.Floor(gridPos.X), (int) MathF.Floor(gridPos.Y));
            var lift = solidity.GetSolidity(grid, tile) == TileSolidity.Half ? halfHeight : 0f;

            _billboards.Add(new Billboard(uid, candidate.Comp, screenX, cam.Y, worldRot - gridRot, lift));
        }

        DrawFlats(handle, camera, depth, width, height, horizon);

        // No depth buffer exists (all vertex positions are Vector2), so sort far-to-near and let
        // the painter's algorithm resolve overlap.
        _billboards.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        foreach (var billboard in _billboards)
        {
            var scale = height / billboard.Depth / PixelsPerMeter;

            // Sprite centre. Half a tile up from its base, less however far its support raises it.
            var screenY = horizon + height / billboard.Depth * (camera.Height - 0.5f - billboard.Lift);

            var direction = (billboard.GridRotation - camera.Yaw).GetDir();

            handle.DrawEntity(
                billboard.Uid,
                new Vector2(billboard.ScreenX, screenY),
                new Vector2(scale, scale),
                billboard.GridRotation,
                overrideDirection: direction,
                sprite: billboard.Sprite);
        }
    }

    /// <summary>
    /// Draws floor-flat entities as quads lying on the floor plane.
    /// </summary>
    /// <remarks>
    /// Flat colour rather than the entity's sprite: sampling RSI frames means handing the shared
    /// sprite atlas to <see cref="DrawingHandleBase.DrawPrimitives"/>, which corrupts rendering
    /// globally. The point here is conveying "there is solid footing on this tile", which colour
    /// alone does. Drawn before billboards so items sit on top of the floor rather than under it.
    /// </remarks>
    private void DrawFlats(
        DrawingHandleScreen handle,
        FirstPersonCamera camera,
        float[] depth,
        int width,
        int height,
        float horizon)
    {
        if (_flats.Count == 0)
            return;

        // Nearest first, then by draw depth. Distinct tiles project to disjoint screen quads so
        // their relative order is free, which lets distance drive it — and that matters because the
        // vertex buffer is finite: when it fills, the tiles given up are the distant ones rather
        // than whichever happened to sort last. Two entities on the *same* tile share a distance,
        // so the draw-depth tiebreak still lays subfloor pipes under the catwalk above them.
        _flats.Sort(static (a, b) =>
        {
            var byDistance = a.Distance.CompareTo(b.Distance);
            return byDistance != 0 ? byDistance : a.DrawDepth.CompareTo(b.DrawDepth);
        });

        _vertCount = 0;

        foreach (var flat in _flats)
        {
            var t = flat.Tile;

            if (!ProjectFloor(camera, new Vector2(t.X, t.Y), width, height, horizon, out var a) ||
                !ProjectFloor(camera, new Vector2(t.X + 1, t.Y), width, height, horizon, out var b) ||
                !ProjectFloor(camera, new Vector2(t.X + 1, t.Y + 1), width, height, horizon, out var c) ||
                !ProjectFloor(camera, new Vector2(t.X, t.Y + 1), width, height, horizon, out var d))
            {
                continue;
            }

            // Cull tiles that project entirely off one side. Without this, a tile beside the camera
            // projects to an extreme screen X and smears a long wedge across the floor.
            if ((a.X < 0 && b.X < 0 && c.X < 0 && d.X < 0) ||
                (a.X > width && b.X > width && c.X > width && d.X > width))
            {
                continue;
            }

            // Occlusion against walls, sampled at the tile centre rather than per pixel.
            var centreX = (int) ((a.X + c.X) / 2f);
            var centreDepth = flat.Distance;
            if (centreX >= 0 && centreX < depth.Length && depth[centreX] < centreDepth)
                continue;

            var shade = Math.Max(1f / (1f + centreDepth * 0.04f), 0.45f);
            var baseColor = flat.DrawDepth <= (int) Content.Shared.DrawDepth.DrawDepth.BelowFloor
                ? SubfloorColor
                : WalkwayColor;

            var color = Color.FromSrgb(new Color(
                baseColor.R * shade,
                baseColor.G * shade,
                baseColor.B * shade,
                1f));

            if (_vertCount + 6 > _flatVerts.Length)
                break;

            AppendTriangle(a, b, c, color);
            AppendTriangle(a, c, d, color);
        }

        if (_vertCount > 0)
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, _flatVerts.AsSpan(0, _vertCount));
    }

    /// <summary>
    /// Projects a point on the floor plane into screen space. False if it is nearer than the near
    /// plane, which discards the whole tile.
    /// </summary>
    /// <remarks>
    /// Discarding is deliberate, and clamping the depth instead is a trap that was tried and
    /// reverted. Screen Y goes as <c>height / depth</c>, so a corner just in front of the camera
    /// lands hundreds of pixels below the horizon while the tile's far corners sit at ordinary
    /// positions — the quad becomes a long diagonal wedge sweeping across the view as you walk.
    /// Losing the tile underfoot, which is mostly out of frame anyway, is much cheaper than that.
    /// </remarks>
    private static bool ProjectFloor(
        FirstPersonCamera camera,
        Vector2 gridPos,
        int width,
        int height,
        float horizon,
        out Vector2 screen)
    {
        screen = default;

        var cam = camera.WorldToCamera(gridPos);
        if (cam.Y <= NearPlane)
            return false;

        screen = new Vector2(
            width / 2f * (1f + cam.X / cam.Y),
            horizon + height / cam.Y * camera.Height);

        return true;
    }

    private void AppendTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        _flatVerts[_vertCount++] = new DrawVertexUV2DColor(a, Vector2.Zero, color);
        _flatVerts[_vertCount++] = new DrawVertexUV2DColor(b, Vector2.Zero, color);
        _flatVerts[_vertCount++] = new DrawVertexUV2DColor(c, Vector2.Zero, color);
    }

    private readonly record struct FlatTile(Vector2i Tile, int DrawDepth, float Distance);

    private readonly record struct Billboard(
        EntityUid Uid,
        SpriteComponent Sprite,
        float ScreenX,
        float Depth,
        Angle GridRotation,
        float Lift);
}
