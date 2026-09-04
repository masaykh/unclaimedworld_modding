using System;
using System.Text;

namespace UW.Tools.MgfxTranscode;

/// <summary>
/// Minimal DXBC container reader, just enough to answer one question: could this shader be
/// recompiled for MonoGame's OpenGL profile, which caps at Shader Model 3?
///
/// The presence of an <c>Aon9</c> chunk only tells you the shader was compiled at a
/// <c>*_4_0_level_9_x</c> profile (that chunk holds the DX9 fallback bytecode, which is what
/// MojoShader consumes). Its ABSENCE means the shader was compiled at plain <c>4_0</c> or
/// higher - but that is NOT the same as needing SM4 *features*. `level_9_1` is roughly
/// `ps_2_0` with extra restrictions, while `ps_3_0` is far more capable, so a shader that the
/// studio had to move off `level_9_1` may still fit comfortably inside `ps_3_0`.
///
/// The <c>STAT</c> chunk carries the real numbers - instruction count, temp registers, flow
/// control - which is what decides that. Layout is the documented D3D11 shader statistics
/// blob; only the leading fields are read here.
/// </summary>
internal static class DxbcInfo
{
    internal readonly record struct ShaderStats(
        bool IsDxbc,
        string ShaderModel,
        bool HasAon9,
        int InstructionCount,
        int TempRegisterCount,
        int StaticFlowControl,
        int DynamicFlowControl,
        int TextureInstructions)
    {
        /// <summary>
        /// Whether the shipped blob carries a STAT chunk at all.
        ///
        /// IMPORTANT: for this game's effects it never does. mgfxc strips reflection and
        /// statistics chunks, so every shipped shader contains only DXBC + ISGN/OSGN + SHDR
        /// (+ Aon9 where it was built at a level_9_x profile). That means instruction counts,
        /// temp-register counts and flow-control counts are simply NOT RECOVERABLE from the
        /// shipped content, and no ps_3_0 feasibility verdict can honestly be derived from it.
        ///
        /// The only way to find out whether a shader fits ps_3_0 is to recover its HLSL and
        /// ask fxc. The recorded shader model (ps_4_0 etc.) tells you what the studio compiled
        /// at, which is not the same as what the shader needs - `compile ps_4_0` is simply what
        /// one writes when targeting DX11.
        /// </summary>
        public bool HasStatistics => InstructionCount > 0 || TempRegisterCount > 0;

        public string Ps30Verdict =>
            !IsDxbc ? "not DXBC"
            : !HasStatistics ? "unknown (no STAT chunk - stripped by mgfxc)"
            : DynamicFlowControl > 0 ? "RISK: dynamic flow control"
            : InstructionCount > 512 ? "NO: " + InstructionCount + " instructions > 512"
            : TempRegisterCount > 32 ? "NO: " + TempRegisterCount + " temps > 32"
            : "fits ps_3_0 limits";
    }

    public static ShaderStats Read(byte[] bytecode)
    {
        if (bytecode.Length < 32 || Encoding.ASCII.GetString(bytecode, 0, 4) != "DXBC")
        {
            return new ShaderStats(false, "-", false, 0, 0, 0, 0, 0);
        }

        int chunkCount = BitConverter.ToInt32(bytecode, 28);
        string model = "?";
        bool hasAon9 = false;
        int instr = 0, temps = 0, staticFlow = 0, dynFlow = 0, texInstr = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            int offsetPos = 32 + i * 4;
            if (offsetPos + 4 > bytecode.Length) break;
            int chunkOffset = BitConverter.ToInt32(bytecode, offsetPos);
            if (chunkOffset + 8 > bytecode.Length) continue;

            string fourcc = Encoding.ASCII.GetString(bytecode, chunkOffset, 4);
            int dataOffset = chunkOffset + 8;

            switch (fourcc)
            {
                case "Aon9":
                    hasAon9 = true;
                    break;

                case "SHDR":
                case "SHEX":
                {
                    // First dword of the shader chunk is the version token:
                    // bits 0-3 minor, 4-7 major, 16-31 program type.
                    if (dataOffset + 4 > bytecode.Length) break;
                    uint version = BitConverter.ToUInt32(bytecode, dataOffset);
                    int minor = (int)(version & 0xF);
                    int major = (int)((version >> 4) & 0xF);
                    uint programType = version >> 16;
                    string stage = programType switch
                    {
                        0 => "ps", 1 => "vs", 2 => "gs", 3 => "hs", 4 => "ds", 5 => "cs",
                        _ => "?",
                    };
                    model = stage + "_" + major + "_" + minor;
                    break;
                }

                case "STAT":
                {
                    // D3D11 shader statistics: InstructionCount, TempRegisterCount, DefCount,
                    // DclCount, TextureNormalInstructions, TextureLoadInstructions,
                    // TextureCompInstructions, TextureBiasInstructions,
                    // TextureGradientInstructions, FloatInstructionCount, IntInstructionCount,
                    // UintInstructionCount, StaticFlowControlCount, DynamicFlowControlCount, ...
                    if (dataOffset + 14 * 4 > bytecode.Length) break;
                    instr = BitConverter.ToInt32(bytecode, dataOffset + 0);
                    temps = BitConverter.ToInt32(bytecode, dataOffset + 4);
                    texInstr = BitConverter.ToInt32(bytecode, dataOffset + 16)
                             + BitConverter.ToInt32(bytecode, dataOffset + 20)
                             + BitConverter.ToInt32(bytecode, dataOffset + 24)
                             + BitConverter.ToInt32(bytecode, dataOffset + 28)
                             + BitConverter.ToInt32(bytecode, dataOffset + 32);
                    staticFlow = BitConverter.ToInt32(bytecode, dataOffset + 48);
                    dynFlow = BitConverter.ToInt32(bytecode, dataOffset + 52);
                    break;
                }
            }
        }

        return new ShaderStats(true, model, hasAon9, instr, temps, staticFlow, dynFlow, texInstr);
    }
}
