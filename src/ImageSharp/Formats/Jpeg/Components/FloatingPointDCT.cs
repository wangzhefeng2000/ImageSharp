// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

// ReSharper disable InconsistentNaming
namespace SixLabors.ImageSharp.Formats.Jpeg.Components
{
    /// <summary>
    /// Contains floating point forward and inverse DCT implementations
    /// </summary>
    /// <remarks>
    /// Based on "Arai, Agui and Nakajima" algorithm.
    /// </remarks>
    internal static partial class FloatingPointDCT
    {
#pragma warning disable SA1310, SA1311, IDE1006 // naming rules violation warnings
        private static readonly Vector4 mm128_F_0_7071 = new(0.707106781f);
        private static readonly Vector4 mm128_F_0_3826 = new(0.382683433f);
        private static readonly Vector4 mm128_F_0_5411 = new(0.541196100f);
        private static readonly Vector4 mm128_F_1_3065 = new(1.306562965f);

        private static readonly Vector4 mm128_F_1_4142 = new(1.414213562f);
        private static readonly Vector4 mm128_F_1_8477 = new(1.847759065f);
        private static readonly Vector4 mm128_F_n1_0823 = new(-1.082392200f);
        private static readonly Vector4 mm128_F_n2_6131 = new(-2.613125930f);
#pragma warning restore SA1310, SA1311, IDE1006

        /// <summary>
        /// Gets adjustment table for quantization tables.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Current IDCT and FDCT implementations are based on  Arai, Agui,
        /// and Nakajima's algorithm. Both DCT methods does not
        /// produce finished DCT output, final step is fused into the
        /// quantization step. Quantization and de-quantization coefficients
        /// must be multiplied by these values.
        /// </para>
        /// <para>
        /// Given values were generated by formula:
        /// <code>
        /// scalefactor[row] * scalefactor[col], where
        /// scalefactor[0] = 1
        /// scalefactor[k] = cos(k*PI/16) * sqrt(2)    for k=1..7
        /// </code>
        /// </para>
        /// </remarks>
        private static readonly float[] AdjustmentCoefficients = new float[]
        {
            1f, 1.3870399f, 1.306563f, 1.1758755f, 1f, 0.78569496f, 0.5411961f, 0.27589938f,
            1.3870399f, 1.9238797f, 1.812255f, 1.6309863f, 1.3870399f, 1.0897902f, 0.7506606f, 0.38268346f,
            1.306563f, 1.812255f, 1.707107f, 1.5363555f, 1.306563f, 1.02656f, 0.7071068f, 0.36047992f,
            1.1758755f, 1.6309863f, 1.5363555f, 1.3826833f, 1.1758755f, 0.9238795f, 0.63637924f, 0.32442334f,
            1f, 1.3870399f, 1.306563f, 1.1758755f, 1f, 0.78569496f, 0.5411961f, 0.27589938f,
            0.78569496f, 1.0897902f, 1.02656f, 0.9238795f, 0.78569496f, 0.61731654f, 0.42521507f, 0.21677275f,
            0.5411961f, 0.7506606f, 0.7071068f, 0.63637924f, 0.5411961f, 0.42521507f, 0.29289323f, 0.14931567f,
            0.27589938f, 0.38268346f, 0.36047992f, 0.32442334f, 0.27589938f, 0.21677275f, 0.14931567f, 0.076120466f,
        };

        /// <summary>
        /// Adjusts given quantization table for usage with <see cref="TransformIDCT"/>.
        /// </summary>
        /// <param name="quantTable">Quantization table to adjust.</param>
        public static void AdjustToIDCT(ref Block8x8F quantTable)
        {
            ref float tableRef = ref Unsafe.As<Block8x8F, float>(ref quantTable);
            ref float multipliersRef = ref MemoryMarshal.GetReference<float>(AdjustmentCoefficients);
            for (nint i = 0; i < Block8x8F.Size; i++)
            {
                ref float elemRef = ref Unsafe.Add(ref tableRef, i);
                elemRef = 0.125f * elemRef * Unsafe.Add(ref multipliersRef, i);
            }

            // Spectral macroblocks are transposed before quantization
            // so we must transpose quantization table
            quantTable.TransposeInplace();
        }

        /// <summary>
        /// Adjusts given quantization table for usage with <see cref="TransformFDCT"/>.
        /// </summary>
        /// <param name="quantTable">Quantization table to adjust.</param>
        public static void AdjustToFDCT(ref Block8x8F quantTable)
        {
            ref float tableRef = ref Unsafe.As<Block8x8F, float>(ref quantTable);
            ref float multipliersRef = ref MemoryMarshal.GetReference<float>(AdjustmentCoefficients);
            for (nint i = 0; i < Block8x8F.Size; i++)
            {
                ref float elemRef = ref Unsafe.Add(ref tableRef, i);
                elemRef = 0.125f / (elemRef * Unsafe.Add(ref multipliersRef, i));
            }

            // Spectral macroblocks are not transposed before quantization
            // Transpose is done after quantization at zig-zag stage
            // so we must transpose quantization table
            quantTable.TransposeInplace();
        }

        /// <summary>
        /// Apply 2D floating point IDCT inplace.
        /// </summary>
        /// <remarks>
        /// Input block must be dequantized with quantization table
        /// adjusted by <see cref="AdjustToIDCT"/>.
        /// </remarks>
        /// <param name="block">Input block.</param>
        public static void TransformIDCT(ref Block8x8F block)
        {
            if (Avx.IsSupported)
            {
                IDCT8x8_Avx(ref block);
            }
            else
            {
                IDCT_Vector4(ref block);
            }
        }

        /// <summary>
        /// Apply 2D floating point IDCT inplace.
        /// </summary>
        /// <remarks>
        /// Input block must be quantized after this method with quantization
        /// table adjusted by <see cref="AdjustToFDCT"/>.
        /// </remarks>
        /// <param name="block">Input block.</param>
        public static void TransformFDCT(ref Block8x8F block)
        {
            if (Avx.IsSupported)
            {
                FDCT8x8_Avx(ref block);
            }
            else
            {
                FDCT_Vector4(ref block);
            }
        }

        /// <summary>
        /// Apply floating point IDCT inplace using <see cref="Vector4"/> API.
        /// </summary>
        /// <remarks>
        /// This method can be used even if there's no SIMD intrinsics available
        /// as <see cref="Vector4"/> can be compiled to scalar instructions.
        /// </remarks>
        /// <param name="transposedBlock">Input block.</param>
        private static void IDCT_Vector4(ref Block8x8F transposedBlock)
        {
            // First pass - process columns
            IDCT8x4_Vector4(ref transposedBlock.V0L);
            IDCT8x4_Vector4(ref transposedBlock.V0R);

            // Second pass - process rows
            transposedBlock.TransposeInplace();
            IDCT8x4_Vector4(ref transposedBlock.V0L);
            IDCT8x4_Vector4(ref transposedBlock.V0R);

            // Applies 1D floating point IDCT inplace on 8x4 part of 8x8 block
            static void IDCT8x4_Vector4(ref Vector4 vecRef)
            {
                // Even part
                Vector4 tmp0 = Unsafe.Add(ref vecRef, 0 * 2);
                Vector4 tmp1 = Unsafe.Add(ref vecRef, 2 * 2);
                Vector4 tmp2 = Unsafe.Add(ref vecRef, 4 * 2);
                Vector4 tmp3 = Unsafe.Add(ref vecRef, 6 * 2);

                Vector4 z5 = tmp0;
                Vector4 tmp10 = z5 + tmp2;
                Vector4 tmp11 = z5 - tmp2;

                Vector4 tmp13 = tmp1 + tmp3;
                Vector4 tmp12 = ((tmp1 - tmp3) * mm128_F_1_4142) - tmp13;

                tmp0 = tmp10 + tmp13;
                tmp3 = tmp10 - tmp13;
                tmp1 = tmp11 + tmp12;
                tmp2 = tmp11 - tmp12;

                // Odd part
                Vector4 tmp4 = Unsafe.Add(ref vecRef, 1 * 2);
                Vector4 tmp5 = Unsafe.Add(ref vecRef, 3 * 2);
                Vector4 tmp6 = Unsafe.Add(ref vecRef, 5 * 2);
                Vector4 tmp7 = Unsafe.Add(ref vecRef, 7 * 2);

                Vector4 z13 = tmp6 + tmp5;
                Vector4 z10 = tmp6 - tmp5;
                Vector4 z11 = tmp4 + tmp7;
                Vector4 z12 = tmp4 - tmp7;

                tmp7 = z11 + z13;
                tmp11 = (z11 - z13) * mm128_F_1_4142;

                z5 = (z10 + z12) * mm128_F_1_8477;

                tmp10 = (z12 * mm128_F_n1_0823) + z5;
                tmp12 = (z10 * mm128_F_n2_6131) + z5;

                tmp6 = tmp12 - tmp7;
                tmp5 = tmp11 - tmp6;
                tmp4 = tmp10 - tmp5;

                Unsafe.Add(ref vecRef, 0 * 2) = tmp0 + tmp7;
                Unsafe.Add(ref vecRef, 7 * 2) = tmp0 - tmp7;
                Unsafe.Add(ref vecRef, 1 * 2) = tmp1 + tmp6;
                Unsafe.Add(ref vecRef, 6 * 2) = tmp1 - tmp6;
                Unsafe.Add(ref vecRef, 2 * 2) = tmp2 + tmp5;
                Unsafe.Add(ref vecRef, 5 * 2) = tmp2 - tmp5;
                Unsafe.Add(ref vecRef, 3 * 2) = tmp3 + tmp4;
                Unsafe.Add(ref vecRef, 4 * 2) = tmp3 - tmp4;
            }
        }

        /// <summary>
        /// Apply floating point FDCT inplace using <see cref="Vector4"/> API.
        /// </summary>
        /// <param name="block">Input block.</param>
        private static void FDCT_Vector4(ref Block8x8F block)
        {
            // First pass - process columns
            FDCT8x4_Vector4(ref block.V0L);
            FDCT8x4_Vector4(ref block.V0R);

            // Second pass - process rows
            block.TransposeInplace();
            FDCT8x4_Vector4(ref block.V0L);
            FDCT8x4_Vector4(ref block.V0R);

            // Applies 1D floating point FDCT inplace on 8x4 part of 8x8 block
            static void FDCT8x4_Vector4(ref Vector4 vecRef)
            {
                Vector4 tmp0 = Unsafe.Add(ref vecRef, 0) + Unsafe.Add(ref vecRef, 14);
                Vector4 tmp7 = Unsafe.Add(ref vecRef, 0) - Unsafe.Add(ref vecRef, 14);
                Vector4 tmp1 = Unsafe.Add(ref vecRef, 2) + Unsafe.Add(ref vecRef, 12);
                Vector4 tmp6 = Unsafe.Add(ref vecRef, 2) - Unsafe.Add(ref vecRef, 12);
                Vector4 tmp2 = Unsafe.Add(ref vecRef, 4) + Unsafe.Add(ref vecRef, 10);
                Vector4 tmp5 = Unsafe.Add(ref vecRef, 4) - Unsafe.Add(ref vecRef, 10);
                Vector4 tmp3 = Unsafe.Add(ref vecRef, 6) + Unsafe.Add(ref vecRef, 8);
                Vector4 tmp4 = Unsafe.Add(ref vecRef, 6) - Unsafe.Add(ref vecRef, 8);

                // Even part
                Vector4 tmp10 = tmp0 + tmp3;
                Vector4 tmp13 = tmp0 - tmp3;
                Vector4 tmp11 = tmp1 + tmp2;
                Vector4 tmp12 = tmp1 - tmp2;

                Unsafe.Add(ref vecRef, 0) = tmp10 + tmp11;
                Unsafe.Add(ref vecRef, 8) = tmp10 - tmp11;

                Vector4 z1 = (tmp12 + tmp13) * mm128_F_0_7071;
                Unsafe.Add(ref vecRef, 4) = tmp13 + z1;
                Unsafe.Add(ref vecRef, 12) = tmp13 - z1;

                // Odd part
                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;

                Vector4 z5 = (tmp10 - tmp12) * mm128_F_0_3826;
                Vector4 z2 = (mm128_F_0_5411 * tmp10) + z5;
                Vector4 z4 = (mm128_F_1_3065 * tmp12) + z5;
                Vector4 z3 = tmp11 * mm128_F_0_7071;

                Vector4 z11 = tmp7 + z3;
                Vector4 z13 = tmp7 - z3;

                Unsafe.Add(ref vecRef, 10) = z13 + z2;
                Unsafe.Add(ref vecRef, 6) = z13 - z2;
                Unsafe.Add(ref vecRef, 2) = z11 + z4;
                Unsafe.Add(ref vecRef, 14) = z11 - z4;
            }
        }
    }
}
