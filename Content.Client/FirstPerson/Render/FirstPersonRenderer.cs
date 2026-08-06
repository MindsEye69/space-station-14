using System.Numerics;
using Content.Client.FirstPerson.Camera;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Map.Components;

namespace Content.Client.FirstPerson.Render;

/// <summary>
/// Runs the first-person passes in order: floors, then walls, then entities sorted far-to-near.
/// </summary>
/// <remarks>
/// Everything is computed in the player's grid-local space, not world space. Tile indices and
/// anchored-entity lookups are grid-local by definition, and stations can sit at an arbitrary
/// position and rotation, so the camera is converted into that frame once per frame here.
/// </remarks>
public sealed class FirstPersonRenderer
{
    private readonly IEntityManager _entMan;
    private readonly SharedTransformSystem _xform;
    private readonly TileSolidityCache _solidity;
    private readonly WallPass _wallPass;
    private readonly FloorPass _floorPass;
    private readonly EntityPass _entityPass;

    private readonly FirstPersonCamera _gridCamera = new();

    public FirstPersonRenderer(IEntityManager entMan)
    {
        _entMan = entMan;
        _xform = entMan.System<SharedTransformSystem>();

        _solidity = new TileSolidityCache(entMan);
        _wallPass = new WallPass(_solidity);
        _floorPass = new FloorPass();
        _entityPass = new EntityPass(entMan, entMan.System<EntityLookupSystem>(), _xform);
    }

    /// <summary>
    /// Perpendicular distance to the wall in a screen column, or <see cref="float.MaxValue"/> if the
    /// ray reached nothing. Used to stop crosshair picking from reaching through walls.
    /// </summary>
    public float GetWallDepth(int column)
    {
        var depth = _wallPass.Depth;
        return column >= 0 && column < depth.Length ? depth[column] : float.MaxValue;
    }

    public void Render(
        DrawingHandleScreen handle,
        FirstPersonCamera camera,
        EntityUid player,
        int width,
        int height,
        int maxRange,
        Texture wallTexture,
        bool drawEntities,
        bool drawHalfHeight,
        float halfHeight)
    {
        _wallPass.DrawHalfHeight = drawHalfHeight;
        _wallPass.HalfHeight = halfHeight;

        _floorPass.Render(handle, camera, width, height);

        var xform = _entMan.GetComponent<TransformComponent>(player);
        if (xform.GridUid is not { } gridUid || !_entMan.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            return;

        var gridRot = _xform.GetWorldRotation(gridUid);
        var invGrid = _xform.GetInvWorldMatrix(gridUid);

        _gridCamera.Position = Vector2.Transform(camera.Position, invGrid);
        _gridCamera.Yaw = camera.Yaw - gridRot;
        _gridCamera.PitchPixels = camera.PitchPixels;
        _gridCamera.Height = camera.Height;
        _gridCamera.FovDegrees = camera.FovDegrees;

        _solidity.Clear();
        _wallPass.Render(handle, _gridCamera, (gridUid, grid), width, height, maxRange, wallTexture);

        if (!drawEntities)
            return;

        _entityPass.Render(
            handle,
            _gridCamera,
            (gridUid, grid),
            _solidity,
            invGrid,
            gridRot,
            xform.MapID,
            player,
            _wallPass.Depth,
            width,
            height,
            maxRange,
            halfHeight);
    }
}
