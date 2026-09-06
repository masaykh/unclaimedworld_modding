using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace UWGame.Port;

/// <summary>
/// PORT DEVIATION 14 (see PORTING-NOTES.md).
///
/// Rewrites a loaded model's vertex buffers so that no vertex element uses
/// <see cref="VertexElementFormat.Color"/> for anything other than a colour.
///
/// Every skinned model in the game declares its blend weights as
/// <c>BlendWeight0:Color@36</c> - four packed bytes, which XNA defines as mapping to 0..1.
/// MonoGame's OpenGL backend decides whether to normalize a vertex attribute from the element's
/// USAGE rather than its FORMAT:
///
///     if (element.VertexElementUsage == VertexElementUsage.Color) return true;
///     if ((uint)(element.VertexElementFormat - 8) &lt;= 1u) return true;   // NormalizedShort2/4
///     return false;
///
/// so a Color-format element whose usage is BlendWeight is bound with normalized = false and the
/// weights reach the shader as 0..255. skinFX multiplies bone-transformed positions by those
/// weights, so every skinned mesh was scaled by about 255 and drawn as shards fanning across the
/// screen. WindowsDX is unaffected: it maps the same element to R8G8B8A8_UNORM, which normalizes.
///
/// The fix is applied to the DATA rather than worked around in the shader, because the shader is
/// shared with WindowsDX and is correct as it stands - and because changing the element's usage
/// to Color, the other way to satisfy MonoGame's test, would stop the attribute matching the
/// shader's BlendWeight input at all.
///
/// Widening to <see cref="VertexElementFormat.Vector4"/> rather than picking another normalized
/// format is deliberate: the only other normalized options are NormalizedShort2/4, which would
/// still be a lossy repack, whereas four floats are exact and cost 12 bytes per vertex on a few
/// hundred models.
/// </summary>
internal static class SkinnedVertexCompat
{
    /// <summary>
    /// Models already processed. ContentManager caches by asset name and hands back the same
    /// Model instance, so without this a repeated Load would convert the same buffers twice -
    /// and the second pass would read floats as if they were packed bytes.
    /// </summary>
    private static readonly HashSet<Model> Converted = new();

    public static void Normalize(Model model, GraphicsDevice device)
    {
        if (model == null) return;
        lock (Converted)
        {
            if (!Converted.Add(model)) return;
        }

        // One VertexBuffer is routinely shared by several ModelMeshParts, each drawing a range of
        // it through its own VertexOffset. So the replacement is made once per buffer and handed
        // to every part that referenced it - and the original is NOT disposed. Disposing it broke
        // every part after the first, and the buffer belongs to the ContentManager, which
        // disposes it on Unload.
        var replacements = new Dictionary<VertexBuffer, VertexBuffer>();

        foreach (ModelMesh mesh in model.Meshes)
        {
            foreach (ModelMeshPart part in mesh.MeshParts)
            {
                VertexBuffer source = part.VertexBuffer;
                if (source == null) continue;

                if (replacements.TryGetValue(source, out VertexBuffer already))
                {
                    part.VertexBuffer = already;
                    continue;
                }

                VertexElement[] elements = source.VertexDeclaration.GetVertexElements();
                if (!NeedsWidening(elements)) continue;

                try
                {
                    VertexBuffer widened = Widen(source, elements, device);
                    replacements[source] = widened;
                    part.VertexBuffer = widened;
                }
                catch (Exception e)
                {
                    // Leave the model exactly as it was rather than half-converted. Skinning will
                    // be wrong, but a wrong mesh is a far better failure than an exception during
                    // content load, which takes the whole world with it.
                    Console.WriteLine("SkinnedVertexCompat: left a vertex buffer unconverted - " +
                                      e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }

    private static bool NeedsWidening(VertexElement[] elements)
    {
        foreach (VertexElement e in elements)
        {
            if (e.VertexElementFormat == VertexElementFormat.Color &&
                e.VertexElementUsage != VertexElementUsage.Color)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Copies the buffer into a new one where each miscast Color element has become four floats,
    /// appended at the end so every other element keeps its offset.
    /// </summary>
    private static VertexBuffer Widen(VertexBuffer source, VertexElement[] elements, GraphicsDevice device)
    {
        int oldStride = source.VertexDeclaration.VertexStride;
        var newElements = new List<VertexElement>();
        var widened = new List<(int SourceOffset, int DestinationOffset)>();

        int nextOffset = oldStride;
        foreach (VertexElement e in elements)
        {
            if (e.VertexElementFormat == VertexElementFormat.Color &&
                e.VertexElementUsage != VertexElementUsage.Color)
            {
                newElements.Add(new VertexElement(
                    nextOffset, VertexElementFormat.Vector4, e.VertexElementUsage, e.UsageIndex));
                widened.Add((e.Offset, nextOffset));
                nextOffset += 16;
            }
            else
            {
                newElements.Add(e);
            }
        }

        int newStride = nextOffset;
        int vertexCount = source.VertexCount;

        var oldData = new byte[oldStride * vertexCount];
        source.GetData(oldData);

        var newData = new byte[newStride * vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            Buffer.BlockCopy(oldData, v * oldStride, newData, v * newStride, oldStride);

            foreach (var (sourceOffset, destinationOffset) in widened)
            {
                int from = v * oldStride + sourceOffset;
                int to = v * newStride + destinationOffset;
                // Color is packed R,G,B,A in ascending bytes, and the shader reads the attribute
                // as (x,y,z,w) in that same order.
                for (int c = 0; c < 4; c++)
                {
                    BitConverter.TryWriteBytes(
                        new Span<byte>(newData, to + c * 4, 4), oldData[from + c] / 255f);
                }
            }
        }

        var destination = new VertexBuffer(
            device, new VertexDeclaration(newStride, newElements.ToArray()),
            vertexCount, BufferUsage.None);
        destination.SetData(newData);
        return destination;
    }
}
