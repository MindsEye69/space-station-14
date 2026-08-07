using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// ImageSharp brings its own Color, and this file needs both it and the engine's.
using Color = Robust.Shared.Maths.Color;

namespace Content.Client.FirstPerson.Render;

/// <summary>
/// A colour for each face of a structure, derived from its top-down sprite.
/// </summary>
public readonly record struct SurfaceTint(Color Top, Color Side)
{
    /// <summary>
    /// Opaque pixels the sampled frame had, used to pick between a sprite's layers. An
    /// <c>_unlit</c> overlay or a panel effect is a handful of pixels over a transparent field; the
    /// base sprite is most of the tile. Taking the layer with the most opaque pixels picks the
    /// object rather than the glow laid over it.
    /// </summary>
    public int Coverage { get; init; }
}

public interface ISurfacePalette
{
    /// <summary>
    /// The tint for an entity's sprite, if one was derived. False for anything whose art was not
    /// sampled — see the path filter on the implementation.
    /// </summary>
    bool TryGetTint(SpriteComponent sprite, out SurfaceTint tint);
}

/// <summary>
/// Derives a per-material colour for the top and the sides of a structure by sampling its sprite
/// when the RSI loads.
/// </summary>
/// <remarks>
/// SS14 draws everything from above, so a sprite is a picture of the top face and nothing else. Two
/// different questions therefore get two different answers out of the same image, and measuring real
/// furniture is what showed they could not be the same number:
///
/// <list type="bullet">
/// <item><b>Top</b> is the <i>dominant</i> colour — quantise to 4 bits per channel, take the fullest
/// bucket, average within it. That finds the material actually covering the surface.</item>
/// <item><b>Side</b> is the mean of an <i>edge ring</i> just inside the sprite's opaque bounding box,
/// which is literally the pixels nearest to where the real side would be.</item>
/// </list>
///
/// The carpet table settles it. Its sprite is a wooden table with a green carpet laid on top: the
/// dominant colour is <c>#006600</c> and the edge ring is <c>#4F2E18</c>. Both are right — the top of
/// that table <i>is</i> green and its sides <i>are</i> wood — and a single sample would have to be
/// wrong about one of them. A plain average is wrong about both, giving the muddy <c>#30460F</c> that
/// exists nowhere on the object.
///
/// Dark pixels are excluded from the dominant bucket, and that is load-bearing rather than tidying.
/// SS14 sprites carry heavy near-black linework and shading, and on 13% of the structures that can
/// become geometry the linework won the bucket outright — a bookshelf came out <c>#1C0B08</c> when
/// it is plainly brown wood, a seed extractor <c>#050303</c> when it is grey. Excluding pixels below
/// <see cref="MinDominantLuminance"/> takes that to zero across all 433 of them, and leaves every
/// case that already worked untouched: the carpet table is still <c>#006600</c>. Restricting to the
/// sprite's interior instead was tried and only reached 7%, because the dark is spread through the
/// shading rather than confined to the outline. The cost is that a genuinely black object reads as
/// dark grey, which is a good trade.
///
/// The side is then forced darker than the top. That relationship is imposed, not discovered — over
/// 433 structures the rim is naturally darker only 41% of the time, so the art cannot be relied on
/// for it, and a vertical face brighter than the horizontal one above it reads as broken.
///
/// Sampling happens on <see cref="IResourceCache.OnRsiLoaded"/> because the engine hands over the
/// decoded atlas there, already on the CPU. Re-opening the PNGs later would also work — the sandbox
/// does permit <c>Image.Load</c> — but it would decode every sheet a second time to learn what the
/// engine had just finished reading.
/// </remarks>
internal sealed class SurfacePalette : ISurfacePalette, IPostInjectInit
{
    /// <summary>
    /// Only structures are sampled. Everything drawn as first-person geometry lives here, and the
    /// alternative is doing this work for all ~3000 RSIs in the game at startup to serve a mode that
    /// is off by default. Widen it if geometry ever comes from elsewhere.
    /// </summary>
    private const string SampledPath = "/Textures/Structures";

    /// <summary>Alpha below which a pixel is treated as absent rather than dark.</summary>
    private const byte AlphaThreshold = 128;

    /// <summary>How far inside the opaque bounding box counts as the rim, in pixels.</summary>
    private const int EdgeRingDepth = 3;

    /// <summary>
    /// Pixels dimmer than this are kept out of the dominant bucket, so sprite linework cannot win it.
    /// </summary>
    private const float MinDominantLuminance = 30f;

    /// <summary>Ceiling on a side's brightness as a fraction of its top's.</summary>
    private const float SideRelativeLuminance = 0.75f;

    [Dependency] private readonly IResourceCache _resourceCache = default!;

    private readonly Dictionary<RSI, Dictionary<RSI.StateId, SurfaceTint>> _tints = new();

    /// <summary>Reused across states so sampling allocates nothing per RSI.</summary>
    private readonly int[] _histogram = new int[4096];

    public void PostInject()
    {
        _resourceCache.OnRsiLoaded += OnRsiLoaded;
    }

    public bool TryGetTint(SpriteComponent sprite, out SurfaceTint tint)
    {
        tint = default;
        var found = false;

        // Not the first layer that happens to have an RSI — that is often an overlay. Airlocks
        // carry `closing_unlit` and `panel_closing` layers whose frames are nearly empty, and taking
        // one of those painted the surface with whatever few pixels it had.
        foreach (var spriteLayer in sprite.AllLayers)
        {
            if (spriteLayer is not SpriteComponent.Layer layer)
                continue;

            if (layer.ActualRsi is not { } rsi)
                continue;

            if (!_tints.TryGetValue(rsi, out var states) || !states.TryGetValue(layer.State, out var candidate))
                continue;

            if (!found || candidate.Coverage > tint.Coverage)
            {
                tint = candidate;
                found = true;
            }
        }

        return found;
    }

    private void OnRsiLoaded(RsiLoadedEventArgs obj)
    {
        if (obj.Atlas is not Image<Rgba32> atlas)
            return;

        var rsi = obj.Resource.RSI;

        if (!rsi.Path.ToString().StartsWith(SampledPath, StringComparison.Ordinal))
            return;

        var (frameWidth, frameHeight) = rsi.Size;
        var pixels = atlas.GetPixelSpan();
        var states = new Dictionary<RSI.StateId, SurfaceTint>();

        foreach (var (stateId, directions) in obj.AtlasOffsets)
        {
            // Direction 0, frame 0. Every direction of a structure shares its material, and the
            // point of this is the material rather than the pose.
            if (directions.Length == 0 || directions[0].Length == 0)
                continue;

            var offset = directions[0][0];

            if (TrySample(pixels, atlas.Width, atlas.Height, offset.X, offset.Y, frameWidth, frameHeight, out var tint))
                states[stateId] = tint;
        }

        if (states.Count > 0)
            _tints[rsi] = states;
    }

    private bool TrySample(
        ReadOnlySpan<Rgba32> pixels,
        int atlasWidth,
        int atlasHeight,
        int originX,
        int originY,
        int frameWidth,
        int frameHeight,
        out SurfaceTint tint)
    {
        tint = default;

        if (originX < 0 || originY < 0 ||
            originX + frameWidth > atlasWidth ||
            originY + frameHeight > atlasHeight)
        {
            return false;
        }

        Array.Clear(_histogram);

        var opaque = 0;
        int minX = frameWidth, maxX = -1, minY = frameHeight, maxY = -1;

        for (var y = 0; y < frameHeight; y++)
        {
            var row = (originY + y) * atlasWidth + originX;

            for (var x = 0; x < frameWidth; x++)
            {
                var p = pixels[row + x];
                if (p.A < AlphaThreshold)
                    continue;

                opaque++;

                if (Luminance(p) >= MinDominantLuminance)
                    _histogram[Bucket(p)]++;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (opaque == 0)
            return false;

        var bestBucket = 0;
        var bestCount = -1;

        for (var i = 0; i < _histogram.Length; i++)
        {
            if (_histogram[i] > bestCount)
            {
                bestCount = _histogram[i];
                bestBucket = i;
            }
        }

        // Second pass: average within the winning bucket, and average the rim. Quantising alone
        // would snap the colour to a 16-level grid, which is visible as banding between materials.
        long domR = 0, domG = 0, domB = 0, domN = 0;
        long edgeR = 0, edgeG = 0, edgeB = 0, edgeN = 0;

        for (var y = 0; y < frameHeight; y++)
        {
            var row = (originY + y) * atlasWidth + originX;

            for (var x = 0; x < frameWidth; x++)
            {
                var p = pixels[row + x];
                if (p.A < AlphaThreshold)
                    continue;

                if (Luminance(p) >= MinDominantLuminance && Bucket(p) == bestBucket)
                {
                    domR += p.R; domG += p.G; domB += p.B; domN++;
                }

                if (x <= minX + EdgeRingDepth - 1 || x >= maxX - EdgeRingDepth + 1 ||
                    y <= minY + EdgeRingDepth - 1 || y >= maxY - EdgeRingDepth + 1)
                {
                    edgeR += p.R; edgeG += p.G; edgeB += p.B; edgeN++;
                }
            }
        }

        // Every pixel was below the luminance floor, so the sprite really is black. Fall back to the
        // rim, which is a mean and therefore never collapses onto the linework.
        if (domN == 0)
        {
            if (edgeN == 0)
                return false;

            var flat = new Color((byte) (edgeR / edgeN), (byte) (edgeG / edgeN), (byte) (edgeB / edgeN));
            tint = new SurfaceTint(flat, Darken(flat, SideRelativeLuminance)) { Coverage = opaque };
            return true;
        }

        var top = new Color((byte) (domR / domN), (byte) (domG / domN), (byte) (domB / domN));

        // A sprite thinner than twice the ring depth has no interior, so the rim is the whole thing.
        var side = edgeN > 0
            ? new Color((byte) (edgeR / edgeN), (byte) (edgeG / edgeN), (byte) (edgeB / edgeN))
            : top;

        // Keep the rim's hue, which carries the material, but not its brightness if it would leave a
        // vertical face lighter than the horizontal one it holds up.
        var ceiling = Luminance(top) * SideRelativeLuminance;
        var sideLuminance = Luminance(side);

        if (sideLuminance > ceiling && sideLuminance > 0f)
            side = Darken(side, ceiling / sideLuminance);

        tint = new SurfaceTint(top, side) { Coverage = opaque };
        return true;
    }

    private static Color Darken(Color color, float factor)
    {
        return new Color(
            (byte) (color.RByte * factor),
            (byte) (color.GByte * factor),
            (byte) (color.BByte * factor));
    }

    private static float Luminance(Rgba32 p)
    {
        return 0.2126f * p.R + 0.7152f * p.G + 0.0722f * p.B;
    }

    private static float Luminance(Color c)
    {
        return 0.2126f * c.RByte + 0.7152f * c.GByte + 0.0722f * c.BByte;
    }

    private static int Bucket(Rgba32 p)
    {
        return ((p.R >> 4) << 8) | ((p.G >> 4) << 4) | (p.B >> 4);
    }
}
