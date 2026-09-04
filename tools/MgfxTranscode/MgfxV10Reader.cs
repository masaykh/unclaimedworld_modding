using System;
using System.IO;
using System.Text;

namespace UW.Tools.MgfxTranscode;

/// <summary>
/// A read-only parser for MGFX v10, written to mirror MonoGame 3.8.2's
/// Graphics/Effect/Effect.cs (ReadEffect / ReadParameters / ReadAnnotations / ReadPasses) and
/// Graphics/Shader/Shader.cs field for field.
///
/// This exists purely to validate <see cref="MgfxRewriter"/> output without needing a GPU or a
/// live MonoGame GraphicsDevice. It is only trustworthy because it is first pointed at genuine
/// v10 files produced by MonoGame's OWN writer - the six stock effects embedded in
/// MonoGame.Framework.dll 3.8.2 (AlphaTest, Basic, DualTexture, EnvironmentMap, Skinned,
/// Sprite). If it parses those exactly, the v10 layout encoded here is correct; only then does
/// "it parses our transcoded effects exactly" mean anything.
///
/// "Exactly" means: consumes every byte, and the trailing int32 equals the MGFX signature -
/// which is precisely the completeness check MonoGame's own Effect constructor performs.
/// </summary>
internal sealed class MgfxV10Reader
{
    private static readonly int MgfxSignature = BitConverter.IsLittleEndian ? 0x5846474D : 0x4D474658;

    private readonly BinaryReader _r;

    private MgfxV10Reader(BinaryReader r) => _r = r;

    internal readonly record struct Stats(
        int Version,
        int Profile,
        int EffectKey,
        int ConstantBuffers,
        int Shaders,
        int Parameters,
        int Techniques,
        int Passes,
        int ShaderBytecodeBytes)
    {
        public override string ToString() =>
            $"v{Version} profile={Profile} cbuffers={ConstantBuffers} shaders={Shaders} " +
            $"params={Parameters} techniques={Techniques} passes={Passes} " +
            $"bytecode={ShaderBytecodeBytes}B";
    }

    private int _constantBuffers, _shaders, _parameters, _techniques, _passes, _bytecodeBytes;

    /// <summary>One parsed shader block, for <see cref="ReadShaders"/>.</summary>
    internal readonly record struct ShaderBlock(bool IsVertexShader, byte[] Bytecode, int Samplers, int Attributes);

    private readonly System.Collections.Generic.List<ShaderBlock> _blocks = new();

    /// <summary>
    /// Parses <paramref name="blob"/> as MGFX v10. Throws <see cref="InvalidDataException"/>
    /// unless the whole blob is consumed and the tail signature is present.
    /// </summary>
    public static Stats Validate(byte[] blob)
    {
        using var ms = new MemoryStream(blob, writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        var parser = new MgfxV10Reader(reader);

        int signature = reader.ReadInt32();
        if (signature != MgfxSignature)
            throw new InvalidDataException("Not an MGFX blob (bad header signature).");

        int version = reader.ReadByte();
        int profile = reader.ReadByte();
        int effectKey = reader.ReadInt32();

        if (version != MgfxRewriter.TargetVersion)
            throw new InvalidDataException($"Expected MGFX v{MgfxRewriter.TargetVersion}, found v{version}.");

        parser.ReadBody();

        // MonoGame's Effect ctor reads this tail and throws if it is not the signature; it is
        // how it confirms the blob was parsed correctly.
        int tail = reader.ReadInt32();
        if (tail != MgfxSignature)
            throw new InvalidDataException(
                $"Tail signature missing or wrong (0x{tail:X8}); MonoGame would reject this effect.");

        if (ms.Position != ms.Length)
            throw new InvalidDataException(
                $"Parsed {ms.Position} of {ms.Length} bytes - {ms.Length - ms.Position} trailing byte(s).");

        return new Stats(version, profile, effectKey, parser._constantBuffers, parser._shaders,
            parser._parameters, parser._techniques, parser._passes, parser._bytecodeBytes);
    }

    private void ReadBody()
    {
        _constantBuffers = _r.ReadInt32();
        for (int i = 0; i < _constantBuffers; i++)
        {
            _r.ReadString();                        // name
            _r.ReadInt16();                         // sizeInBytes
            int parameterCount = _r.ReadInt32();
            for (int j = 0; j < parameterCount; j++)
            {
                _r.ReadInt32();                     // parameter index
                _r.ReadUInt16();                    // offset
            }
        }

        _shaders = _r.ReadInt32();
        for (int i = 0; i < _shaders; i++)
            ReadShader();

        _parameters = ReadParameters();

        _techniques = _r.ReadInt32();
        for (int i = 0; i < _techniques; i++)
        {
            _r.ReadString();                        // name
            ReadAnnotations();
            int passCount = _r.ReadInt32();
            _passes += passCount;
            for (int p = 0; p < passCount; p++)
            {
                _r.ReadString();                    // name
                ReadAnnotations();
                _r.ReadInt32();                     // vertex shader index (negative = none)
                _r.ReadInt32();                     // pixel shader index  (negative = none)
                if (_r.ReadBoolean()) ReadBlendState();
                if (_r.ReadBoolean()) ReadDepthStencilState();
                if (_r.ReadBoolean()) ReadRasterizerState();
            }
        }
    }

    private int ReadParameters()
    {
        int count = _r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            _r.ReadByte();                          // EffectParameterClass
            byte type = _r.ReadByte();              // EffectParameterType
            _r.ReadString();                        // name
            _r.ReadString();                        // semantic
            ReadAnnotations();
            int rowCount = _r.ReadByte();
            int columnCount = _r.ReadByte();

            int elements = ReadParameters();
            int structMembers = ReadParameters();

            if (elements != 0 || structMembers != 0)
                continue;

            int values = rowCount * columnCount;
            switch (type)
            {
                case 1: // Bool
                case 2: // Int32
                    for (int v = 0; v < values; v++) _r.ReadInt32();
                    break;
                case 3: // Single
                    for (int v = 0; v < values; v++) _r.ReadSingle();
                    break;
                case 4: // String
                    throw new NotSupportedException("MGFX string parameters are not supported.");
                default:
                    break;
            }
        }
        return count;
    }

    private void ReadAnnotations() => _r.ReadInt32();   // count only; no payload is stored

    private void ReadShader()
    {
        bool isVertexShader = _r.ReadBoolean();
        int bytecodeLength = _r.ReadInt32();
        _bytecodeBytes += bytecodeLength;
        byte[] bytecode = _r.ReadBytes(bytecodeLength);
        if (bytecode.Length != bytecodeLength)
            throw new InvalidDataException("Shader bytecode length runs past the end of the blob.");

        int samplerCount = _r.ReadByte();
        for (int s = 0; s < samplerCount; s++)
        {
            _r.ReadByte();                          // SamplerType
            _r.ReadByte();                          // textureSlot
            _r.ReadByte();                          // samplerSlot
            if (_r.ReadBoolean())
            {
                _r.ReadByte();                      // AddressU
                _r.ReadByte();                      // AddressV
                _r.ReadByte();                      // AddressW
                _r.ReadBytes(4);                    // BorderColor
                _r.ReadByte();                      // Filter
                _r.ReadInt32();                     // MaxAnisotropy
                _r.ReadInt32();                     // MaxMipLevel
                _r.ReadSingle();                    // MipMapLevelOfDetailBias
            }
            _r.ReadString();                        // name
            _r.ReadByte();                          // parameter index
        }

        int cbufferCount = _r.ReadByte();
        _r.ReadBytes(cbufferCount);

        int attributeCount = _r.ReadByte();
        for (int a = 0; a < attributeCount; a++)
        {
            _r.ReadString();                        // name
            _r.ReadByte();                          // VertexElementUsage
            _r.ReadByte();                          // index
            _r.ReadInt16();                         // location
        }

        _blocks.Add(new ShaderBlock(isVertexShader, bytecode, samplerCount, attributeCount));
    }

    /// <summary>Parses the blob and returns its shader blocks, for DXBC analysis.</summary>
    public static System.Collections.Generic.List<ShaderBlock> ReadShaders(byte[] blob)
    {
        using var ms = new MemoryStream(blob, writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        var parser = new MgfxV10Reader(reader);
        reader.ReadInt32();   // signature
        reader.ReadByte();    // version
        reader.ReadByte();    // profile
        reader.ReadInt32();   // effect key
        parser.ReadBody();
        return parser._blocks;
    }

    private void ReadBlendState()
    {
        _r.ReadBytes(3);        // Alpha blend function / destination / source
        _r.ReadBytes(4);        // BlendFactor
        _r.ReadBytes(3);        // Color blend function / destination / source
        _r.ReadBytes(4);        // ColorWriteChannels 0..3
        _r.ReadInt32();         // MultiSampleMask
    }

    private void ReadDepthStencilState()
    {
        _r.ReadBytes(4);        // counter-clockwise stencil ops / function
        _r.ReadBoolean();       // DepthBufferEnable
        _r.ReadByte();          // DepthBufferFunction
        _r.ReadBoolean();       // DepthBufferWriteEnable
        _r.ReadInt32();         // ReferenceStencil
        _r.ReadByte();          // StencilDepthBufferFail
        _r.ReadBoolean();       // StencilEnable
        _r.ReadByte();          // StencilFail
        _r.ReadByte();          // StencilFunction
        _r.ReadInt32();         // StencilMask
        _r.ReadByte();          // StencilPass
        _r.ReadInt32();         // StencilWriteMask
        _r.ReadBoolean();       // TwoSidedStencilMode
    }

    private void ReadRasterizerState()
    {
        _r.ReadByte();          // CullMode
        _r.ReadSingle();        // DepthBias
        _r.ReadByte();          // FillMode
        _r.ReadBoolean();       // MultiSampleAntiAlias
        _r.ReadBoolean();       // ScissorTestEnable
        _r.ReadSingle();        // SlopeScaleDepthBias
    }
}
