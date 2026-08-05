using Content.Shared.Physics;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

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

    /// <summary>Floor to ceiling. The ray stops here.</summary>
    Full = 2,
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

    private readonly SharedMapSystem _mapSystem;
    private readonly EntityQuery<FixturesComponent> _fixtureQuery;
    private readonly EntityQuery<SpriteComponent> _spriteQuery;

    private readonly Dictionary<Vector2i, TileSolidity> _cache = new();

    public TileSolidityCache(IEntityManager entMan)
    {
        _mapSystem = entMan.System<SharedMapSystem>();
        _fixtureQuery = entMan.GetEntityQuery<FixturesComponent>();
        _spriteQuery = entMan.GetEntityQuery<SpriteComponent>();
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public TileSolidity GetSolidity(Entity<MapGridComponent> grid, Vector2i tile)
    {
        if (_cache.TryGetValue(tile, out var cached))
            return cached;

        var solidity = TileSolidity.None;
        var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(grid.Owner, grid.Comp, tile);

        while (anchored.MoveNext(out var uid))
        {
            // If the player cannot see it, it must not be a wall. Subfloor pipes and cabling sit on
            // ordinary tiles carrying real hard fixtures, and SubFloorHideSystem hides their sprite
            // rather than removing them — so a purely physics-based test turns every plated floor
            // into a thicket of geometry.
            if (!_spriteQuery.TryGetComponent(uid.Value, out var sprite) || !sprite.Visible)
                continue;

            if (!_fixtureQuery.TryGetComponent(uid.Value, out var fixtures))
                continue;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (!fixture.Hard)
                    continue;

                if ((fixture.CollisionLayer & (int) FullMask) != 0)
                {
                    solidity = TileSolidity.Full;
                    break;
                }

                if ((fixture.CollisionLayer & (int) HalfMask) != 0)
                    solidity = TileSolidity.Half;
            }

            // Nothing taller than this exists, so no need to keep looking.
            if (solidity == TileSolidity.Full)
                break;
        }

        _cache[tile] = solidity;
        return solidity;
    }
}
