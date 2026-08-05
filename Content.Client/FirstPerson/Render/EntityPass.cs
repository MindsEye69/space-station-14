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

    private readonly EntityLookupSystem _lookup;
    private readonly SharedTransformSystem _xform;

    private readonly HashSet<Entity<SpriteComponent>> _candidates = new();
    private readonly List<Billboard> _billboards = new();

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

    private readonly record struct Billboard(
        EntityUid Uid,
        SpriteComponent Sprite,
        float ScreenX,
        float Depth,
        Angle GridRotation,
        float Lift);
}
