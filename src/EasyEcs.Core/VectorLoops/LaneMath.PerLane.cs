using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

public static partial class LaneMath
{
    /// <summary>
    /// Math with no vector form in .NET 10 (or none bit-exact), called once per lane inside a generated vector loop: the
    /// rest of the method's body stays in vectors and each lane gets the scalar function's bits. The generator takes
    /// this form only where the body does enough besides the calls for the vectors to pay for the lanes' round trip
    /// through memory (Kernel's gate); <c>TVector</c> is the loop's vector of floats or doubles.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class PerLane
    {
        /// <summary>A function of one float or double, as <see cref="MathF"/> and <see cref="Math"/> compute it.</summary>
        public interface IUnary
        {
            /// <summary>The function of a float.</summary>
            static abstract float Of(float x);

            /// <summary>The function of a double.</summary>
            static abstract double Of(double x);
        }

        /// <summary>A function of two floats or doubles, as <see cref="MathF"/> and <see cref="Math"/> compute it.</summary>
        public interface IBinary
        {
            /// <summary>The function of two floats.</summary>
            static abstract float Of(float x, float y);

            /// <summary>The function of two doubles.</summary>
            static abstract double Of(double x, double y);
        }

        /// <summary><typeparamref name="TFunction"/> of each lane of <paramref name="x"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TVector Of<TFunction, TVector>(TVector x) where TFunction : IUnary where TVector : struct
        {
            Unsafe.SkipInit(out TVector result);
            if (IsSingle<TVector>())
            {
                ref float to = ref Unsafe.As<TVector, float>(ref result), from = ref Unsafe.As<TVector, float>(ref x);
                for (int i = 0; i < Unsafe.SizeOf<TVector>() / sizeof(float); i++) Unsafe.Add(ref to, i) = TFunction.Of(Unsafe.Add(ref from, i));
            }
            else
            {
                ref double to = ref Unsafe.As<TVector, double>(ref result), from = ref Unsafe.As<TVector, double>(ref x);
                for (int i = 0; i < Unsafe.SizeOf<TVector>() / sizeof(double); i++) Unsafe.Add(ref to, i) = TFunction.Of(Unsafe.Add(ref from, i));
            }
            return result;
        }

        /// <summary><typeparamref name="TFunction"/> of each pair of lanes of <paramref name="x"/> and <paramref name="y"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TVector Of<TFunction, TVector>(TVector x, TVector y) where TFunction : IBinary where TVector : struct
        {
            Unsafe.SkipInit(out TVector result);
            if (IsSingle<TVector>())
            {
                ref float to = ref Unsafe.As<TVector, float>(ref result), a = ref Unsafe.As<TVector, float>(ref x), b = ref Unsafe.As<TVector, float>(ref y);
                for (int i = 0; i < Unsafe.SizeOf<TVector>() / sizeof(float); i++) Unsafe.Add(ref to, i) = TFunction.Of(Unsafe.Add(ref a, i), Unsafe.Add(ref b, i));
            }
            else
            {
                ref double to = ref Unsafe.As<TVector, double>(ref result), a = ref Unsafe.As<TVector, double>(ref x), b = ref Unsafe.As<TVector, double>(ref y);
                for (int i = 0; i < Unsafe.SizeOf<TVector>() / sizeof(double); i++) Unsafe.Add(ref to, i) = TFunction.Of(Unsafe.Add(ref a, i), Unsafe.Add(ref b, i));
            }
            return result;
        }

        private static bool IsSingle<TVector>() =>
            typeof(TVector) == typeof(Vector<float>) || typeof(TVector) == typeof(Vector128<float>)
            || typeof(TVector) == typeof(Vector256<float>) || typeof(TVector) == typeof(Vector512<float>);

        /// <summary><see cref="MathF.Sin"/>, <see cref="Math.Sin"/>.</summary>
        public struct Sin : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Sin(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Sin(x);
        }

        /// <summary><see cref="MathF.Cos"/>, <see cref="Math.Cos"/>.</summary>
        public struct Cos : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Cos(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Cos(x);
        }

        /// <summary><see cref="MathF.Tan"/>, <see cref="Math.Tan"/>.</summary>
        public struct Tan : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Tan(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Tan(x);
        }

        /// <summary><see cref="MathF.Asin"/>, <see cref="Math.Asin"/>.</summary>
        public struct Asin : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Asin(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Asin(x);
        }

        /// <summary><see cref="MathF.Acos"/>, <see cref="Math.Acos"/>.</summary>
        public struct Acos : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Acos(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Acos(x);
        }

        /// <summary><see cref="MathF.Atan"/>, <see cref="Math.Atan"/>.</summary>
        public struct Atan : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Atan(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Atan(x);
        }

        /// <summary><see cref="MathF.Sinh"/>, <see cref="Math.Sinh"/>.</summary>
        public struct Sinh : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Sinh(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Sinh(x);
        }

        /// <summary><see cref="MathF.Cosh"/>, <see cref="Math.Cosh"/>.</summary>
        public struct Cosh : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Cosh(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Cosh(x);
        }

        /// <summary><see cref="MathF.Tanh"/>, <see cref="Math.Tanh"/>.</summary>
        public struct Tanh : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Tanh(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Tanh(x);
        }

        /// <summary><see cref="MathF.Exp"/>, <see cref="Math.Exp"/>.</summary>
        public struct Exp : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Exp(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Exp(x);
        }

        /// <summary><see cref="MathF.Log(float)"/>, <see cref="Math.Log(double)"/>.</summary>
        public struct Log : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Log(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Log(x);
        }

        /// <summary><see cref="MathF.Log2"/>, <see cref="Math.Log2"/>.</summary>
        public struct Log2 : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Log2(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Log2(x);
        }

        /// <summary><see cref="MathF.Log10"/>, <see cref="Math.Log10"/>.</summary>
        public struct Log10 : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Log10(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Log10(x);
        }

        /// <summary><see cref="MathF.Cbrt"/>, <see cref="Math.Cbrt"/>.</summary>
        public struct Cbrt : IUnary
        {
            /// <inheritdoc/>
            public static float Of(float x) => MathF.Cbrt(x);

            /// <inheritdoc/>
            public static double Of(double x) => Math.Cbrt(x);
        }

        /// <summary><see cref="MathF.Atan2"/>, <see cref="Math.Atan2"/>.</summary>
        public struct Atan2 : IBinary
        {
            /// <inheritdoc/>
            public static float Of(float y, float x) => MathF.Atan2(y, x);

            /// <inheritdoc/>
            public static double Of(double y, double x) => Math.Atan2(y, x);
        }

        /// <summary><see cref="MathF.Pow"/>, <see cref="Math.Pow"/>.</summary>
        public struct Pow : IBinary
        {
            /// <inheritdoc/>
            public static float Of(float x, float y) => MathF.Pow(x, y);

            /// <inheritdoc/>
            public static double Of(double x, double y) => Math.Pow(x, y);
        }
    }
}
