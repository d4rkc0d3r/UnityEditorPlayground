using System.Collections.Generic;
using UnityEngine;

namespace d4rkpl4y3r.MeshVis
{
    public readonly struct OrientedBounds
    {
        public OrientedBounds(Vector3 center, Quaternion rotation, Vector3 size)
        {
            Center = center;
            Rotation = rotation;
            Size = size;
        }

        public Vector3 Center { get; }
        public Quaternion Rotation { get; }
        public Vector3 Size { get; }
    }

    public static class PcaOrientedBounds
    {
        private const int PowerIterationCount = 16;
        private const float Epsilon = 1e-6f;

        public static bool TryCreate(IReadOnlyList<Vector3> points, out OrientedBounds bounds)
        {
            bounds = default;
            if (points == null || points.Count == 0)
            {
                return false;
            }

            var centroid = ComputeCentroid(points);
            if (points.Count == 1)
            {
                bounds = new OrientedBounds(points[0], Quaternion.identity, Vector3.zero);
                return true;
            }

            var covariance = ComputeCovariance(points, centroid);
            var axis0 = GetDominantAxis(covariance);
            var axis1 = GetSecondaryAxis(covariance, axis0);
            var axis2 = Vector3.Cross(axis0, axis1);
            if (!TryNormalize(ref axis2))
            {
                GetPerpendicularBasis(axis0, out axis1, out axis2);
            }
            else
            {
                axis1 = Vector3.Cross(axis2, axis0).normalized;
            }

            var rotationMatrix = Matrix4x4.identity;
            rotationMatrix.SetColumn(0, new Vector4(axis0.x, axis0.y, axis0.z, 0f));
            rotationMatrix.SetColumn(1, new Vector4(axis1.x, axis1.y, axis1.z, 0f));
            rotationMatrix.SetColumn(2, new Vector4(axis2.x, axis2.y, axis2.z, 0f));
            var rotation = rotationMatrix.rotation;

            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                var offset = points[pointIndex] - centroid;
                var localPoint = new Vector3(
                    Vector3.Dot(offset, axis0),
                    Vector3.Dot(offset, axis1),
                    Vector3.Dot(offset, axis2));
                min = Vector3.Min(min, localPoint);
                max = Vector3.Max(max, localPoint);
            }

            var localCenter = (min + max) * 0.5f;
            var worldCenter = centroid
                + axis0 * localCenter.x
                + axis1 * localCenter.y
                + axis2 * localCenter.z;

            bounds = new OrientedBounds(worldCenter, rotation, max - min);
            return true;
        }

        private static Vector3 ComputeCentroid(IReadOnlyList<Vector3> points)
        {
            var sum = Vector3.zero;
            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                sum += points[pointIndex];
            }

            return sum / points.Count;
        }

        private static SymmetricMatrix3x3 ComputeCovariance(IReadOnlyList<Vector3> points, Vector3 centroid)
        {
            var xx = 0f;
            var xy = 0f;
            var xz = 0f;
            var yy = 0f;
            var yz = 0f;
            var zz = 0f;
            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                var offset = points[pointIndex] - centroid;
                xx += offset.x * offset.x;
                xy += offset.x * offset.y;
                xz += offset.x * offset.z;
                yy += offset.y * offset.y;
                yz += offset.y * offset.z;
                zz += offset.z * offset.z;
            }

            var scale = 1f / points.Count;
            return new SymmetricMatrix3x3(
                xx * scale,
                xy * scale,
                xz * scale,
                yy * scale,
                yz * scale,
                zz * scale);
        }

        private static Vector3 GetDominantAxis(SymmetricMatrix3x3 covariance)
        {
            var axis = PowerIterate(covariance, GetLargestVarianceAxis(covariance));
            if (!TryNormalize(ref axis))
            {
                axis = Vector3.right;
            }

            return axis;
        }

        private static Vector3 GetSecondaryAxis(SymmetricMatrix3x3 covariance, Vector3 axis0)
        {
            var eigenValue = Vector3.Dot(axis0, covariance.Multiply(axis0));
            var deflated = covariance.Deflate(axis0, eigenValue);
            var axis1 = PowerIterate(deflated, GetMostOrthogonalCardinalAxis(axis0));
            axis1 -= axis0 * Vector3.Dot(axis1, axis0);
            if (!TryNormalize(ref axis1))
            {
                GetPerpendicularBasis(axis0, out axis1, out _);
            }

            return axis1;
        }

        private static Vector3 PowerIterate(SymmetricMatrix3x3 matrix, Vector3 initialAxis)
        {
            var axis = initialAxis;
            if (!TryNormalize(ref axis))
            {
                axis = Vector3.right;
            }

            for (var iteration = 0; iteration < PowerIterationCount; iteration++)
            {
                var multiplied = matrix.Multiply(axis);
                if (!TryNormalize(ref multiplied))
                {
                    break;
                }

                axis = multiplied;
            }

            return axis;
        }

        private static Vector3 GetLargestVarianceAxis(SymmetricMatrix3x3 covariance)
        {
            if (covariance.M00 >= covariance.M11 && covariance.M00 >= covariance.M22)
            {
                return Vector3.right;
            }

            return covariance.M11 >= covariance.M22 ? Vector3.up : Vector3.forward;
        }

        private static Vector3 GetMostOrthogonalCardinalAxis(Vector3 axis)
        {
            var xDot = Mathf.Abs(Vector3.Dot(axis, Vector3.right));
            var yDot = Mathf.Abs(Vector3.Dot(axis, Vector3.up));
            var zDot = Mathf.Abs(Vector3.Dot(axis, Vector3.forward));
            if (xDot <= yDot && xDot <= zDot)
            {
                return Vector3.right;
            }

            return yDot <= zDot ? Vector3.up : Vector3.forward;
        }

        private static void GetPerpendicularBasis(Vector3 axis, out Vector3 perpendicular, out Vector3 binormal)
        {
            perpendicular = Vector3.Cross(axis, GetMostOrthogonalCardinalAxis(axis));
            if (!TryNormalize(ref perpendicular))
            {
                perpendicular = Vector3.up;
            }

            binormal = Vector3.Cross(axis, perpendicular);
            if (!TryNormalize(ref binormal))
            {
                binormal = Vector3.forward;
            }

            perpendicular = Vector3.Cross(binormal, axis).normalized;
        }

        private static bool TryNormalize(ref Vector3 axis)
        {
            var magnitude = axis.magnitude;
            if (magnitude <= Epsilon)
            {
                return false;
            }

            axis /= magnitude;
            return true;
        }

        private readonly struct SymmetricMatrix3x3
        {
            public SymmetricMatrix3x3(float m00, float m01, float m02, float m11, float m12, float m22)
            {
                M00 = m00;
                M01 = m01;
                M02 = m02;
                M11 = m11;
                M12 = m12;
                M22 = m22;
            }

            public float M00 { get; }
            public float M01 { get; }
            public float M02 { get; }
            public float M11 { get; }
            public float M12 { get; }
            public float M22 { get; }

            public Vector3 Multiply(Vector3 vector)
            {
                return new Vector3(
                    M00 * vector.x + M01 * vector.y + M02 * vector.z,
                    M01 * vector.x + M11 * vector.y + M12 * vector.z,
                    M02 * vector.x + M12 * vector.y + M22 * vector.z);
            }

            public SymmetricMatrix3x3 Deflate(Vector3 axis, float eigenValue)
            {
                var xx = eigenValue * axis.x * axis.x;
                var xy = eigenValue * axis.x * axis.y;
                var xz = eigenValue * axis.x * axis.z;
                var yy = eigenValue * axis.y * axis.y;
                var yz = eigenValue * axis.y * axis.z;
                var zz = eigenValue * axis.z * axis.z;
                return new SymmetricMatrix3x3(
                    M00 - xx,
                    M01 - xy,
                    M02 - xz,
                    M11 - yy,
                    M12 - yz,
                    M22 - zz);
            }
        }
    }
}