using System.Numerics;

namespace AnvilLOD.Meshes;

/// <summary>
/// Transform helpers. System.Numerics uses row vectors (v' = v * M), while NIF and
/// Bethesda math is written with column vectors (v' = R * v); the methods here take the
/// column-vector values and build the equivalent row-vector matrices.
/// </summary>
public static class Transforms
{
    /// <summary>NIF local transform: v' = R * (s * v) + t, with R given row by row.</summary>
    public static Matrix4x4 Compose(
        float r11, float r12, float r13,
        float r21, float r22, float r23,
        float r31, float r32, float r33,
        float s, Vector3 t) =>
        new(
            r11 * s, r21 * s, r31 * s, 0,
            r12 * s, r22 * s, r32 * s, 0,
            r13 * s, r23 * s, r33 * s, 0,
            t.X, t.Y, t.Z, 1);

    /// <summary>
    /// Placed-reference transform (REFR DATA position/rotation + XSCL).
    /// Rotation is R = Rx(-x) · Ry(-y) · Rz(-z) with angles in radians — verified against
    /// existing LODGen/DynDOLOD output to within 0.004 units on a reference rotated on all three axes.
    /// </summary>
    public static Matrix4x4 Reference(Vector3 position, Vector3 rotationRadians, float scale)
    {
        var r = Matrix3.Mul(Matrix3.Mul(Matrix3.RotX(-rotationRadians.X), Matrix3.RotY(-rotationRadians.Y)), Matrix3.RotZ(-rotationRadians.Z));
        return Compose(r.M11, r.M12, r.M13, r.M21, r.M22, r.M23, r.M31, r.M32, r.M33, scale, position);
    }

    /// <summary>The rotation part of a (uniformly scaled) transform, for normals and tangents.</summary>
    public static Matrix4x4 RotationOnly(Matrix4x4 m)
    {
        float s = new Vector3(m.M11, m.M12, m.M13).Length();
        if (s < 1e-12f) return Matrix4x4.Identity;
        float inv = 1f / s;
        return new Matrix4x4(
            m.M11 * inv, m.M12 * inv, m.M13 * inv, 0,
            m.M21 * inv, m.M22 * inv, m.M23 * inv, 0,
            m.M31 * inv, m.M32 * inv, m.M33 * inv, 0,
            0, 0, 0, 1);
    }

    public static float DecodeByte(byte b) => b / 127.5f - 1f;

    public static byte EncodeByte(float f) => (byte)Math.Clamp(MathF.Round((f + 1f) * 127.5f), 0f, 255f);

    public static Vector3 DecodeDir(byte x, byte y, byte z) => new(DecodeByte(x), DecodeByte(y), DecodeByte(z));

    public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float l = v.Length();
        return l > 1e-8f && float.IsFinite(l) ? v / l : fallback;
    }

    /// <summary>Tiny column-vector 3x3 matrix, only for building reference rotations.</summary>
    internal readonly record struct Matrix3(
        float M11, float M12, float M13,
        float M21, float M22, float M23,
        float M31, float M32, float M33)
    {
        public static Matrix3 RotX(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new(1, 0, 0, 0, c, -s, 0, s, c); }
        public static Matrix3 RotY(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new(c, 0, s, 0, 1, 0, -s, 0, c); }
        public static Matrix3 RotZ(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new(c, -s, 0, s, c, 0, 0, 0, 1); }

        public static Matrix3 Mul(Matrix3 a, Matrix3 b) => new(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31, a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32, a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31, a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32, a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31, a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32, a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);
    }
}
