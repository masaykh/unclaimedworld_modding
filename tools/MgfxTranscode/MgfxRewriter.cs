using System;
using System.IO;
using System.Text;

namespace UW.Tools.MgfxTranscode;

/// <summary>
/// Rewrites an MGFX v8 container (MonoGame 3.6) as MGFX v10 (MonoGame 3.8.1 - 3.8.5.x).
///
/// The two formats hold exactly the same fields; nothing was added or removed between them.
/// The differences are entirely:
///
///   1. the version byte in the header (8 -> 10);
///   2. eight count/index fields widened from byte to int32 - constant-buffer count,
///      per-buffer parameter count, per-buffer parameter indices, shader count, technique
///      count, annotation counts, pass count, and the two per-pass shader indices;
///   3. a trailing int32 'MGFX' signature that v10 uses to verify it parsed the whole blob.
///
/// Everything else - including every byte of compiled DXBC shader bytecode, all sampler and
/// vertex-attribute metadata, all render-state blocks, and all parameter default data - is
/// copied through verbatim. Notably the entire per-shader block is byte-for-byte identical
/// between the two versions, so the shaders themselves are preserved exactly and no HLSL
/// source is needed.
///
/// The per-pass shader index changes sentinel as well as width: v8 uses byte 255 for "no
/// shader", v10 treats any negative int32 as "no shader".
///
/// v10 is targeted rather than v11 because it is accepted by every MonoGame from 3.8.1
/// onwards: 3.8.1-3.8.4.1 require exactly 10, and 3.8.5.x has MGFXVersion 11 with
/// MGFXMinVersion 10. Emitting 10 therefore works across all of them.
///
/// Layouts were taken from the reader implementations rather than guessed:
///   v8  - decomp/MonoGame.Framework/.../Graphics/Effect.cs and Shader.cs (the shipped,
///         studio-built MonoGame 3.6 assembly, decompiled)
///   v10 - MonoGame v3.8.2 Graphics/Effect/Effect.cs and Graphics/Shader/Shader.cs
///
/// Correctness is self-checking: <see cref="Rewrite"/> fails if the v8 parse does not consume
/// the source blob exactly, which would mean the assumed layout is wrong.
/// </summary>
internal sealed class MgfxRewriter
{
    public const int SourceVersion = 8;
    public const int TargetVersion = 10;

    /// <summary>Little-endian 'MGFX'.</summary>
    private static readonly int MgfxSignature = BitConverter.IsLittleEndian ? 0x5846474D : 0x4D474658;

    private readonly BinaryReader _r;
    private readonly BinaryWriter _w;

    private MgfxRewriter(BinaryReader r, BinaryWriter w)
    {
        _r = r;
        _w = w;
    }

    public int Profile { get; private set; }
    public int EffectKey { get; private set; }

    public static byte[] Rewrite(byte[] source, out int profile, out int effectKey)
    {
        using var input = new MemoryStream(source, writable: false);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);

        var rewriter = new MgfxRewriter(reader, writer);
        rewriter.Run();

        if (input.Position != input.Length)
            throw new InvalidDataException(
                $"MGFX v8 parse consumed {input.Position} of {input.Length} bytes - the assumed " +
                "v8 layout is wrong, refusing to emit a rewritten effect.");

        profile = rewriter.Profile;
        effectKey = rewriter.EffectKey;
        writer.Flush();
        return output.ToArray();
    }

    private void Run()
    {
        // --- header: signature, version, profile, effectKey (10 bytes, same size in v10) ---
        int signature = _r.ReadInt32();
        if (signature != MgfxSignature)
            throw new InvalidDataException("Not an MGFX blob (bad signature).");

        int version = _r.ReadByte();
        if (version != SourceVersion)
            throw new InvalidDataException($"Expected MGFX v{SourceVersion}, found v{version}.");

        Profile = _r.ReadByte();
        EffectKey = _r.ReadInt32();

        _w.Write(MgfxSignature);
        _w.Write((byte)TargetVersion);
        _w.Write((byte)Profile);
        _w.Write(EffectKey);

        // --- constant buffers ---
        int constantBufferCount = WidenCount();
        for (int i = 0; i < constantBufferCount; i++)
        {
            CopyString();                       // name
            CopyInt16();                        // sizeInBytes
            int parameterCount = WidenCount();
            for (int j = 0; j < parameterCount; j++)
            {
                _w.Write((int)_r.ReadByte());   // parameter index: byte -> int32
                CopyUInt16();                   // offset
            }
        }

        // --- shaders (block is identical in v8 and v10) ---
        int shaderCount = WidenCount();
        for (int i = 0; i < shaderCount; i++)
            CopyShader();

        // --- parameters ---
        CopyParameters();

        // --- techniques ---
        int techniqueCount = WidenCount();
        for (int i = 0; i < techniqueCount; i++)
        {
            CopyString();                       // name
            CopyAnnotations();
            int passCount = WidenCount();
            for (int p = 0; p < passCount; p++)
            {
                CopyString();                   // name
                CopyAnnotations();
                CopyShaderIndex();              // vertex shader
                CopyShaderIndex();              // pixel shader
                if (CopyBool()) CopyBlendState();
                if (CopyBool()) CopyDepthStencilState();
                if (CopyBool()) CopyRasterizerState();
            }
        }

        // --- v10 tail signature, used by Effect's ctor to confirm a complete parse ---
        _w.Write(MgfxSignature);
    }

    /// <summary>Parameters nest (array elements and struct members), so this recurses.</summary>
    private int CopyParameters()
    {
        int count = WidenCount();
        for (int i = 0; i < count; i++)
        {
            CopyByte();                                     // EffectParameterClass
            var type = (EffectParameterType)CopyByte();     // EffectParameterType
            CopyString();                                   // name
            CopyString();                                   // semantic
            CopyAnnotations();
            int rowCount = CopyByte();                      // stays a byte in v10
            int columnCount = CopyByte();                   // stays a byte in v10

            int elementCount = CopyParameters();
            int structMemberCount = CopyParameters();

            // Default data is only stored for leaf parameters.
            if (elementCount != 0 || structMemberCount != 0)
                continue;

            int values = rowCount * columnCount;
            switch (type)
            {
                case EffectParameterType.Bool:
                case EffectParameterType.Int32:
                    for (int v = 0; v < values; v++) CopyInt32();
                    break;

                case EffectParameterType.Single:
                    for (int v = 0; v < values; v++) CopySingle();
                    break;

                case EffectParameterType.String:
                    // Both MonoGame versions throw NotSupportedException on load for this, so a
                    // blob containing one could never have shipped in a working game.
                    throw new NotSupportedException("MGFX string parameters are not supported.");

                default:
                    // Void and the texture/sampler types carry no constant-buffer data.
                    break;
            }
        }
        return count;
    }

    /// <summary>
    /// Annotations: only a count is stored. Neither MonoGame version reads any payload for
    /// them (both allocate the array and leave it empty), so widening the count is all there
    /// is to do.
    /// </summary>
    private void CopyAnnotations() => WidenCount();

    private void CopyShader()
    {
        CopyBool();                     // isVertexShader
        int bytecodeLength = CopyInt32();
        CopyBytes(bytecodeLength);      // compiled DXBC, passed through untouched

        int samplerCount = CopyByte();  // stays a byte in v10
        for (int s = 0; s < samplerCount; s++)
        {
            CopyByte();                 // SamplerType
            CopyByte();                 // textureSlot
            CopyByte();                 // samplerSlot
            if (CopyBool())             // has an explicit SamplerState
            {
                CopyByte();             // AddressU
                CopyByte();             // AddressV
                CopyByte();             // AddressW
                CopyBytes(4);           // BorderColor (r, g, b, a)
                CopyByte();             // Filter
                CopyInt32();            // MaxAnisotropy
                CopyInt32();            // MaxMipLevel
                CopySingle();           // MipMapLevelOfDetailBias
            }
            CopyString();               // name
            CopyByte();                 // parameter index
        }

        int cbufferCount = CopyByte();  // stays a byte in v10
        CopyBytes(cbufferCount);        // one byte per constant-buffer index

        int attributeCount = CopyByte(); // stays a byte in v10
        for (int a = 0; a < attributeCount; a++)
        {
            CopyString();               // name
            CopyByte();                 // VertexElementUsage
            CopyByte();                 // index
            CopyInt16();                // location
        }
    }

    private void CopyBlendState()
    {
        CopyByte();     // AlphaBlendFunction
        CopyByte();     // AlphaDestinationBlend
        CopyByte();     // AlphaSourceBlend
        CopyBytes(4);   // BlendFactor (r, g, b, a)
        CopyByte();     // ColorBlendFunction
        CopyByte();     // ColorDestinationBlend
        CopyByte();     // ColorSourceBlend
        CopyByte();     // ColorWriteChannels
        CopyByte();     // ColorWriteChannels1
        CopyByte();     // ColorWriteChannels2
        CopyByte();     // ColorWriteChannels3
        CopyInt32();    // MultiSampleMask
    }

    private void CopyDepthStencilState()
    {
        CopyByte();     // CounterClockwiseStencilDepthBufferFail
        CopyByte();     // CounterClockwiseStencilFail
        CopyByte();     // CounterClockwiseStencilFunction
        CopyByte();     // CounterClockwiseStencilPass
        CopyBool();     // DepthBufferEnable
        CopyByte();     // DepthBufferFunction
        CopyBool();     // DepthBufferWriteEnable
        CopyInt32();    // ReferenceStencil
        CopyByte();     // StencilDepthBufferFail
        CopyBool();     // StencilEnable
        CopyByte();     // StencilFail
        CopyByte();     // StencilFunction
        CopyInt32();    // StencilMask
        CopyByte();     // StencilPass
        CopyInt32();    // StencilWriteMask
        CopyBool();     // TwoSidedStencilMode
    }

    private void CopyRasterizerState()
    {
        CopyByte();     // CullMode
        CopySingle();   // DepthBias
        CopyByte();     // FillMode
        CopyBool();     // MultiSampleAntiAlias
        CopyBool();     // ScissorTestEnable
        CopySingle();   // SlopeScaleDepthBias
    }

    // --- primitives ------------------------------------------------------------------

    /// <summary>A count stored as a byte in v8 and as an int32 in v10.</summary>
    private int WidenCount()
    {
        int count = _r.ReadByte();
        _w.Write(count);
        return count;
    }

    /// <summary>
    /// A per-pass shader index. v8: byte, with 255 meaning "no shader".
    /// v10: int32, with any negative value meaning "no shader".
    /// </summary>
    private void CopyShaderIndex()
    {
        byte index = _r.ReadByte();
        _w.Write(index == 255 ? -1 : index);
    }

    private byte CopyByte()
    {
        byte v = _r.ReadByte();
        _w.Write(v);
        return v;
    }

    private bool CopyBool()
    {
        bool v = _r.ReadBoolean();
        _w.Write(v);
        return v;
    }

    private short CopyInt16()
    {
        short v = _r.ReadInt16();
        _w.Write(v);
        return v;
    }

    private ushort CopyUInt16()
    {
        ushort v = _r.ReadUInt16();
        _w.Write(v);
        return v;
    }

    private int CopyInt32()
    {
        int v = _r.ReadInt32();
        _w.Write(v);
        return v;
    }

    private float CopySingle()
    {
        float v = _r.ReadSingle();
        _w.Write(v);
        return v;
    }

    private string CopyString()
    {
        // BinaryReader/BinaryWriter agree on the 7-bit length prefix + UTF-8 encoding, and both
        // MonoGame versions use them, so a read/write round trip is byte-exact.
        string v = _r.ReadString();
        _w.Write(v);
        return v;
    }

    private void CopyBytes(int count)
    {
        if (count == 0) return;
        byte[] buffer = _r.ReadBytes(count);
        if (buffer.Length != count)
            throw new InvalidDataException($"Expected {count} bytes, got {buffer.Length}.");
        _w.Write(buffer);
    }

    /// <summary>Mirrors Microsoft.Xna.Framework.Graphics.EffectParameterType.</summary>
    private enum EffectParameterType : byte
    {
        Void = 0,
        Bool = 1,
        Int32 = 2,
        Single = 3,
        String = 4,
        Texture = 5,
        Texture1D = 6,
        Texture2D = 7,
        Texture3D = 8,
        TextureCube = 9,
    }
}
