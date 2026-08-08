using System.Numerics;
using Content.Shared.Doors.Components;
using Content.Shared.Physics;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;

namespace Content.Client.FirstPerson.Render;

/// <summary>
/// How much of a tile is blocked, vertically.
/// </summary>
public enum TileSolidity : byte
{
    /// <summary>Nothing stops the ray.</summary>
    None = 0,

    /// <summary>Waist height — a counter or table. The ray passes over it.</summary>
    Half = 1,

    /// <summary>
    /// Chest height — a machine, locker or vending machine. Above the eye, so it blocks the view,
    /// but the ray still passes it so the wall behind stays drawn above its top edge.
    /// </summary>
    Tall = 2,

    /// <summary>Floor to ceiling. The ray stops here.</summary>
    Full = 3,
}

/// <summary>
/// Caches per-tile solidity for one frame.
/// </summary>
/// <remarks>
/// Without this, a 640-column cast re-tests the same tile hundreds of times per frame. Cleared once
/// per frame, not per column.
/// </remarks>
public sealed class TileSolidityCache
{
    /// <summary>
    /// Full-height structures: walls, windows, grilles, closed airlocks.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>OccluderComponent</c>, which means "blocks light" rather than "is a wall".
    /// <see cref="CollisionGroup.GlassLayer"/> is <see cref="CollisionGroup.WallLayer"/> minus
    /// <c>Opaque</c>, so every window and glass airlock — full-height things you cannot walk through
    /// — carries no occluder, and using occluders left holes wherever the station used glass.
    /// </remarks>
    public const CollisionGroup FullMask = CollisionGroup.HighImpassable;

    /// <summary>
    /// Waist-height structures: tables, counters, half walls.
    /// </summary>
    /// <remarks>
    /// Exactly the things you can see and shoot over but not walk through:
    /// <see cref="CollisionGroup.TableLayer"/> and <see cref="CollisionGroup.HalfWallLayer"/> carry
    /// MidImpassable without HighImpassable. SS14 has no z-axis, but this bit pair is a usable
    /// two-level height model that already exists in the data rather than having to be authored.
    /// </remarks>
    public const CollisionGroup HalfMask = CollisionGroup.MidImpassable;

    /// <summary>
    /// What separates a machine from a counter, given both are <see cref="HalfMask"/>.
    /// </summary>
    /// <remarks>
    /// SS14 has no third height bit, but it does have one that correlates perfectly.
    /// <see cref="CollisionGroup.MachineLayer"/> is the only layer carrying
    /// <c>MidImpassable</c> without <c>HighImpassable</c> that is *also* <c>Opaque</c> — because a
    /// machine blocks light and a table does not. <see cref="CollisionGroup.TableLayer"/>,
    /// <see cref="CollisionGroup.HalfWallLayer"/> and <see cref="CollisionGroup.SlipLayer"/> all lack
    /// it, so the test picks out exactly consoles, lockers and vending machines.
    ///
    /// Without this they rendered at counter height, and an anomaly generator became a knee-high
    /// grey lump — worse than untextured, because the silhouette is what you navigate by and this one
    /// was a lie.
    /// </remarks>
    public const CollisionGroup TallMask = CollisionGroup.Opaque;

    /// <summary>
    /// The fixture transform used by <see cref="CoversTileCentre"/>. Anchored entities sit at the
    /// centre of their tile and rotate about it in 90° steps, so fixture-local coordinates are
    /// already tile-relative and the identity transform is the correct one — which is also why the
    /// test needs no entity lookup and costs nothing.
    /// </summary>
    private static readonly Transform Identity = new(Vector2.Zero, 0f);

    /// <summary>
    /// Doors ignore their sampled colour and take this instead.
    /// </summary>
    /// <remarks>
    /// This is the one place the material-accuracy rule is broken on purpose. A standard airlock's
    /// sampled tint is honest and useless: airlock grey against wall grey differs by a few levels, so
    /// a door reads as a faintly different patch of corridor. Doors are what a player navigates by,
    /// and being findable beats being correct.
    ///
    /// The cost, accepted knowingly: departmental airlocks lose their livery, so a security door no
    /// longer reads red. Blending toward the accent instead would keep some of that, but it also
    /// weakens the guarantee — and a guarantee is the point. Revisit if department colour turns out
    /// to matter more in play than door-versus-wall does.
    /// </remarks>
    private static readonly SurfaceTint DoorTint = new(Color.FromHex("#b8923c"), Color.FromHex("#856a2c"));

    private readonly SharedMapSystem _mapSystem;
    private readonly ISurfacePalette _palette;
    private readonly EntityQuery<FixturesComponent> _fixtureQuery;
    private readonly EntityQuery<SpriteComponent> _spriteQuery;
    private readonly EntityQuery<PhysicsComponent> _physicsQuery;
    private readonly EntityQuery<DoorComponent> _doorQuery;

    private readonly Dictionary<Vector2i, TileSurface> _cache = new();

    public TileSolidityCache(IEntityManager entMan, ISurfacePalette palette)
    {
        _mapSystem = entMan.System<SharedMapSystem>();
        _palette = palette;
        _fixtureQuery = entMan.GetEntityQuery<FixturesComponent>();
        _spriteQuery = entMan.GetEntityQuery<SpriteComponent>();
        _physicsQuery = entMan.GetEntityQuery<PhysicsComponent>();
        _doorQuery = entMan.GetEntityQuery<DoorComponent>();
    }

    /// <summary>
    /// Whether an entity's physics actually stops anything.
    /// </summary>
    /// <remarks>
    /// A hard fixture is not enough on its own. Open curtains, cargo pallets, cargo telepads and
    /// security barriers all keep full-tile hard fixtures on real collision layers and simply switch
    /// the body off with <c>canCollide: false</c> — so the fixture says "wall" while the entity stops
    /// nothing, and the view grew waist-high slabs you walk straight through.
    ///
    /// This is the same mistake as testing a collision layer without looking at the fixture's shape:
    /// the renderer has to ask what actually blocks a mob, not read a proxy for it.
    /// </remarks>
    private bool Collides(EntityUid uid)
    {
        return _physicsQuery.TryGetComponent(uid, out var physics) && physics.CanCollide;
    }

    public void Clear()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Whether a fixture stands in the middle of its tile rather than flush against one edge.
    /// </summary>
    /// <remarks>
    /// Both passes treat a tile as all-or-nothing — <see cref="WallPass"/> fills the whole tile and
    /// <see cref="EntityPass"/> drops whatever was filled — so a structure occupying a sliver of one
    /// must not qualify. Railings, fences and windoors are waist-height by layer, but their fixture
    /// is pushed hard against an edge: a railing is <c>-0.49,-0.49,0.49,-0.25</c>, a quarter of a
    /// tile deep. Filling the tile drew a solid brown surface across three sides you can walk
    /// straight through.
    ///
    /// Size cannot separate the two cases. A computer's fixture is half a tile across and a fence
    /// spanning the full width of one is a fifth of a tile deep — both smaller than that railing,
    /// and both genuinely impassable. What every edge-flush structure has in common is that it
    /// leaves the middle of the tile clear, which is exactly what makes it walkable.
    ///
    /// Anything rejected here is not dropped: it fails <c>EntityPass.IsWallGeometry</c> by the same
    /// test and is drawn as a billboard instead. The two must agree, or a railing is excluded from
    /// both passes and becomes invisible.
    /// </remarks>
    public static bool CoversTileCentre(Fixture fixture)
    {
        for (var i = 0; i < fixture.Shape.ChildCount; i++)
        {
            if (fixture.Shape.ComputeAABB(Identity, i).Contains(Vector2.Zero))
                return true;
        }

        return false;
    }

    public TileSolidity GetSolidity(Entity<MapGridComponent> grid, Vector2i tile)
    {
        return GetSurface(grid, tile).Solidity;
    }

    /// <summary>
    /// A tile's height class and, when one could be derived from the entity that supplies it, the
    /// colour of its material.
    /// </summary>
    public TileSurface GetSurface(Entity<MapGridComponent> grid, Vector2i tile)
    {
        if (_cache.TryGetValue(tile, out var cached))
            return cached;

        var solidity = TileSolidity.None;
        SurfaceTint? tint = null;
        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(grid.Owner, grid.Comp, tile);

        while (anchored.MoveNext(out var uid))
        {
            // If the player cannot see it, it must not be a wall. Subfloor pipes and cabling sit on
            // ordinary tiles carrying real hard fixtures, and SubFloorHideSystem hides their sprite
            // rather than removing them — so a purely physics-based test turns every plated floor
            // into a thicket of geometry.
            if (!_spriteQuery.TryGetComponent(uid.Value, out var sprite) || !sprite.Visible)
                continue;

            if (!Collides(uid.Value))
                continue;

            if (!_fixtureQuery.TryGetComponent(uid.Value, out var fixtures))
                continue;

            var before = solidity;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (!fixture.Hard || !CoversTileCentre(fixture))
                    continue;

                if ((fixture.CollisionLayer & (int) FullMask) != 0)
                {
                    solidity = TileSolidity.Full;
                    break;
                }

                if ((fixture.CollisionLayer & (int) HalfMask) == 0)
                    continue;

                var height = (fixture.CollisionLayer & (int) TallMask) != 0
                    ? TileSolidity.Tall
                    : TileSolidity.Half;

                // A tile holding both a machine and a table is as tall as the machine.
                if (height > solidity)
                    solidity = height;
            }

            // Take the colour from whichever entity actually raised the tile's height class, so a
            // tile holding both a table and something shorter is painted as the table.
            if (solidity != before)
            {
                if (_doorQuery.HasComponent(uid.Value))
                    tint = DoorTint;
                else if (_palette.TryGetTint(sprite, out var found))
                    tint = found;
            }

            // Nothing taller than this exists, so no need to keep looking.
            if (solidity == TileSolidity.Full)
                break;
        }

        var surface = new TileSurface(solidity, tint);
        _cache[tile] = surface;
        return surface;
    }
}

/// <summary>
/// What a tile is, vertically, and what it is made of.
/// </summary>
/// <param name="Tint">
/// Null when no colour could be derived — the entity's art was outside the sampled paths, or the
/// tile's height comes from something with no sprite at all. Callers fall back to a flat default.
/// </param>
public readonly record struct TileSurface(TileSolidity Solidity, SurfaceTint? Tint);
