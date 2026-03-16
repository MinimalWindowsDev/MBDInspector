using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StepParser.Parser;

namespace MBDInspector;

public static class StepTessellator
{
    private const int CircleSamples = 32;
    private static readonly Color NoColor = Color.FromArgb(0, 0, 0, 0);

    public static List<(MeshGeometry3D Mesh, Color? FaceColor)> Tessellate(
        IReadOnlyDictionary<int, EntityInstance> data,
        IReadOnlyDictionary<int, Color>? colorMap = null)
    {
        List<FaceMeshItem> faceMeshes = TessellateFaces(data, colorMap);
        var buckets = new Dictionary<Color, (List<Point3D> Positions, List<int> Indices)>();

        foreach (FaceMeshItem faceMesh in faceMeshes)
        {
            Color colorKey = faceMesh.FaceColor ?? NoColor;

            if (!buckets.TryGetValue(colorKey, out var bucket))
            {
                bucket = (new List<Point3D>(), new List<int>());
                buckets[colorKey] = bucket;
            }

            int baseIndex = bucket.Positions.Count;
            bucket.Positions.AddRange(faceMesh.Mesh.Positions);
            bucket.Indices.AddRange(faceMesh.Mesh.TriangleIndices.Select(index => index + baseIndex));
        }

        var result = new List<(MeshGeometry3D Mesh, Color? FaceColor)>(buckets.Count);
        foreach ((Color colorKey, (List<Point3D> positions, List<int> indices)) in buckets)
        {
            if (positions.Count == 0)
            {
                continue;
            }

            result.Add((new MeshGeometry3D
            {
                Positions = new Point3DCollection(positions),
                TriangleIndices = new Int32Collection(indices)
            }, colorKey == NoColor ? null : colorKey));
        }

        return result;
    }

    internal static List<FaceMeshItem> TessellateFaces(
        IReadOnlyDictionary<int, EntityInstance> data,
        IReadOnlyDictionary<int, Color>? colorMap = null)
    {
        var result = new List<FaceMeshItem>();
        foreach (var (faceId, entity) in data)
        {
            if (!TryBuildFaceGeometry(entity, data, out List<Point3D>? positions, out List<int>? indices, out _))
            {
                continue;
            }

            Color? faceColor = colorMap is not null && colorMap.TryGetValue(faceId, out Color color)
                ? color
                : null;

            result.Add(new FaceMeshItem(
                faceId,
                new MeshGeometry3D
                {
                    Positions = new Point3DCollection(positions!),
                    TriangleIndices = new Int32Collection(indices!)
                },
                faceColor));
        }

        return result;
    }

    public static bool TryTessellateFace(
        int faceId,
        IReadOnlyDictionary<int, EntityInstance> data,
        out MeshGeometry3D? mesh)
    {
        mesh = null;
        if (!data.TryGetValue(faceId, out EntityInstance? entity) ||
            !TryBuildFaceGeometry(entity, data, out List<Point3D>? positions, out List<int>? indices, out _))
        {
            return false;
        }

        mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positions!),
            TriangleIndices = new Int32Collection(indices!)
        };
        return true;
    }

    public static bool TryExtractFaceOutline(
        int faceId,
        IReadOnlyDictionary<int, EntityInstance> data,
        out IReadOnlyList<Point3D> outline)
    {
        outline = Array.Empty<Point3D>();
        if (!data.TryGetValue(faceId, out EntityInstance? entity) ||
            !TryGetFacePolygon(entity, data, out List<Point3D>? polygon, out _))
        {
            return false;
        }

        outline = polygon!;
        return true;
    }

    private static bool TryBuildFaceGeometry(
        EntityInstance entity,
        IReadOnlyDictionary<int, EntityInstance> data,
        out List<Point3D>? positions,
        out List<int>? indices,
        out List<Point3D>? polygon)
    {
        positions = null;
        indices = null;
        polygon = null;

        if (!TryGetFacePolygon(entity, data, out polygon, out bool sameSense))
        {
            return false;
        }

        Vector3D desiredNormal = ResolveSurfaceNormal(entity.Parameters[2], data) ?? ComputePolygonNormal(polygon!);
        if (!sameSense)
        {
            desiredNormal = -desiredNormal;
        }

        positions = [];
        indices = [];
        AddFanTriangles(polygon!, desiredNormal, positions, indices);
        return positions.Count > 0;
    }

    private static bool TryGetFacePolygon(
        EntityInstance entity,
        IReadOnlyDictionary<int, EntityInstance> data,
        out List<Point3D>? polygon,
        out bool sameSense)
    {
        polygon = null;
        sameSense = true;

        if (!IsNamed(entity, "ADVANCED_FACE") ||
            entity.Parameters.Count < 4 ||
            entity.Parameters[1] is not Parameter.ListValue boundsParam)
        {
            return false;
        }

        sameSense = entity.Parameters[3] is not Parameter.EnumValue enumValue
                     || !string.Equals(enumValue.Name, "F", StringComparison.OrdinalIgnoreCase);

        foreach (Parameter boundRef in boundsParam.Items)
        {
            if (boundRef is not Parameter.EntityReference boundaryReference ||
                !data.TryGetValue(boundaryReference.Id, out EntityInstance? bound) ||
                bound.Parameters.Count < 2 ||
                bound.Parameters[1] is not Parameter.EntityReference loopRef)
            {
                continue;
            }

            polygon = SampleEdgeLoop(loopRef.Id, data);
            if (polygon.Count >= 3)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddFanTriangles(
        List<Point3D> polygon,
        Vector3D desiredNormal,
        List<Point3D> positions,
        List<int> indices)
    {
        Point3D centroid = Centroid(polygon);
        bool normalIsValid = desiredNormal.Length > 1e-10;

        for (int i = 0; i < polygon.Count; i++)
        {
            Point3D p1 = polygon[i];
            Point3D p2 = polygon[(i + 1) % polygon.Count];

            if (normalIsValid)
            {
                Vector3D autoNormal = Vector3D.CrossProduct(p1 - centroid, p2 - centroid);
                if (Vector3D.DotProduct(autoNormal, desiredNormal) < 0)
                {
                    (p1, p2) = (p2, p1);
                }
            }

            int baseIndex = positions.Count;
            positions.Add(centroid);
            positions.Add(p1);
            positions.Add(p2);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
        }
    }

    private static Vector3D? ResolveSurfaceNormal(
        Parameter surfaceParam,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (surfaceParam is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? surface))
        {
            return null;
        }

        if (IsNamed(surface, "PLANE"))
        {
            if (surface.Parameters.Count < 2)
            {
                return null;
            }

            (_, Vector3D z, _) = ResolveAxis2(surface.Parameters[1], data);
            return z == default ? null : z;
        }

        return null;
    }

    private static List<Point3D> SampleEdgeLoop(
        int loopId,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(loopId, out EntityInstance? loop) ||
            !IsNamed(loop, "EDGE_LOOP") ||
            loop.Parameters.Count < 2 ||
            loop.Parameters[1] is not Parameter.ListValue edgeList)
        {
            return [];
        }

        var result = new List<Point3D>();
        foreach (Parameter edgeRef in edgeList.Items)
        {
            if (edgeRef is not Parameter.EntityReference entityReference ||
                !data.TryGetValue(entityReference.Id, out EntityInstance? orientedEdge) ||
                !IsNamed(orientedEdge, "ORIENTED_EDGE") ||
                orientedEdge.Parameters.Count < 5 ||
                orientedEdge.Parameters[3] is not Parameter.EntityReference curveRef)
            {
                continue;
            }

            bool forward = orientedEdge.Parameters[4] is not Parameter.EnumValue enumValue
                           || !string.Equals(enumValue.Name, "F", StringComparison.OrdinalIgnoreCase);

            List<Point3D> points = SampleEdgeCurve(curveRef.Id, data);
            if (!forward)
            {
                points.Reverse();
            }

            int skip = result.Count > 0 && points.Count > 0 && (points[0] - result[^1]).Length < 1e-6 ? 1 : 0;
            result.AddRange(points.Skip(skip));
        }

        if (result.Count > 1 && (result[0] - result[^1]).Length < 1e-6)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static List<Point3D> SampleEdgeCurve(
        int id,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(id, out EntityInstance? edgeCurve) ||
            !IsNamed(edgeCurve, "EDGE_CURVE") ||
            edgeCurve.Parameters.Count < 4)
        {
            return [];
        }

        Point3D? start = ResolveVertex(edgeCurve.Parameters[1], data);
        Point3D? end = ResolveVertex(edgeCurve.Parameters[2], data);

        if (edgeCurve.Parameters[3] is not Parameter.EntityReference geometryReference ||
            !data.TryGetValue(geometryReference.Id, out EntityInstance? geometry))
        {
            return Compact([start, end]);
        }

        return SampleCurve(geometry, start, end, data);
    }

    private static List<Point3D> SampleCurve(
        EntityInstance geometry,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (IsNamed(geometry, "LINE"))
        {
            return Compact([start, end]);
        }

        if (IsNamed(geometry, "CIRCLE"))
        {
            return SampleCircle(geometry, start, end, data);
        }

        if (IsNamed(geometry, "ELLIPSE"))
        {
            return SampleEllipse(geometry, start, end, data);
        }

        if (IsNamed(geometry, "B_SPLINE_CURVE_WITH_KNOTS") ||
            IsNamed(geometry, "B_SPLINE_CURVE") ||
            IsNamed(geometry, "RATIONAL_B_SPLINE_CURVE"))
        {
            return SampleBSpline(geometry, start, end, data);
        }

        return Compact([start, end]);
    }

    private static List<Point3D> SampleCircle(
        EntityInstance circle,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (circle.Parameters.Count < 3)
        {
            return Compact([start, end]);
        }

        double radius = ToDouble(circle.Parameters[2]);
        if (radius <= 0)
        {
            return Compact([start, end]);
        }

        (Point3D center, Vector3D zDirection, Vector3D xDirection) = ResolveAxis2(circle.Parameters[1], data);
        if (xDirection == default)
        {
            return Compact([start, end]);
        }

        Vector3D yDirection = Vector3D.CrossProduct(zDirection, xDirection);
        yDirection.Normalize();

        bool fullCircle = start is null || end is null || (start.Value - end.Value).Length < 1e-6;
        double t0 = 0;
        double t1 = 2 * Math.PI;
        if (!fullCircle)
        {
            t0 = AngleOnCircle(start!.Value - center, xDirection, yDirection);
            t1 = AngleOnCircle(end!.Value - center, xDirection, yDirection);
            if (t1 <= t0)
            {
                t1 += 2 * Math.PI;
            }
        }

        int sampleCount = fullCircle
            ? CircleSamples
            : Math.Max(4, (int)(Math.Abs(t1 - t0) / (2 * Math.PI) * CircleSamples));

        var points = new List<Point3D>(sampleCount + 1);
        for (int i = 0; i <= sampleCount; i++)
        {
            double t = t0 + (t1 - t0) * i / sampleCount;
            points.Add(center + xDirection * (radius * Math.Cos(t)) + yDirection * (radius * Math.Sin(t)));
        }

        return points;
    }

    private static List<Point3D> SampleEllipse(
        EntityInstance ellipse,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (ellipse.Parameters.Count < 4)
        {
            return Compact([start, end]);
        }

        double semiAxis1 = ToDouble(ellipse.Parameters[2]);
        double semiAxis2 = ToDouble(ellipse.Parameters[3]);
        if (semiAxis1 <= 0 || semiAxis2 <= 0)
        {
            return Compact([start, end]);
        }

        (Point3D center, Vector3D zDirection, Vector3D xDirection) = ResolveAxis2(ellipse.Parameters[1], data);
        if (xDirection == default)
        {
            return Compact([start, end]);
        }

        Vector3D yDirection = Vector3D.CrossProduct(zDirection, xDirection);
        yDirection.Normalize();

        bool fullEllipse = start is null || end is null || (start.Value - end.Value).Length < 1e-6;
        double t0 = 0;
        double t1 = 2 * Math.PI;
        if (!fullEllipse)
        {
            t0 = AngleOnCircle(start!.Value - center, xDirection, yDirection);
            t1 = AngleOnCircle(end!.Value - center, xDirection, yDirection);
            if (t1 <= t0)
            {
                t1 += 2 * Math.PI;
            }
        }

        var points = new List<Point3D>(CircleSamples + 1);
        for (int i = 0; i <= CircleSamples; i++)
        {
            double t = t0 + (t1 - t0) * i / CircleSamples;
            points.Add(center + xDirection * (semiAxis1 * Math.Cos(t)) + yDirection * (semiAxis2 * Math.Sin(t)));
        }

        return points;
    }

    private static List<Point3D> SampleBSpline(
        EntityInstance bspline,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (bspline.Parameters.Count < 3 || bspline.Parameters[2] is not Parameter.ListValue controlPoints)
        {
            return Compact([start, end]);
        }

        var points = new List<Point3D>();
        foreach (Parameter controlPointReference in controlPoints.Items)
        {
            Point3D? point = ResolveCartesianPointParam(controlPointReference, data);
            if (point.HasValue)
            {
                points.Add(point.Value);
            }
        }

        return points.Count >= 2 ? points : Compact([start, end]);
    }

    private static (Point3D Center, Vector3D Z, Vector3D X) ResolveAxis2(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? axis) ||
            axis.Parameters.Count < 4)
        {
            return default;
        }

        Point3D? center = ResolveCartesianPointParam(axis.Parameters[1], data);
        Vector3D z = ResolveDirectionParam(axis.Parameters[2], data);
        Vector3D x = ResolveDirectionParam(axis.Parameters[3], data);
        return (center ?? default, z, x);
    }

    private static Point3D? ResolveVertex(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? vertexPoint) ||
            !IsNamed(vertexPoint, "VERTEX_POINT") ||
            vertexPoint.Parameters.Count < 2)
        {
            return null;
        }

        return ResolveCartesianPointParam(vertexPoint.Parameters[1], data);
    }

    private static Point3D? ResolveCartesianPointParam(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? point) ||
            !IsNamed(point, "CARTESIAN_POINT") ||
            point.Parameters.Count < 2 ||
            point.Parameters[1] is not Parameter.ListValue coords ||
            coords.Items.Count < 2)
        {
            return null;
        }

        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = coords.Items.Count >= 3 ? ToDouble(coords.Items[2]) : 0.0;
        return new Point3D(x, y, z);
    }

    private static Vector3D ResolveDirectionParam(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? direction) ||
            !IsNamed(direction, "DIRECTION") ||
            direction.Parameters.Count < 2 ||
            direction.Parameters[1] is not Parameter.ListValue coords ||
            coords.Items.Count < 3)
        {
            return default;
        }

        var vector = new Vector3D(
            ToDouble(coords.Items[0]),
            ToDouble(coords.Items[1]),
            ToDouble(coords.Items[2]));
        vector.Normalize();
        return vector;
    }

    private static Vector3D ComputePolygonNormal(List<Point3D> polygon)
    {
        var normal = new Vector3D();
        for (int i = 0; i < polygon.Count; i++)
        {
            Point3D current = polygon[i];
            Point3D next = polygon[(i + 1) % polygon.Count];
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }

        if (normal.Length < 1e-10)
        {
            return new Vector3D(0, 0, 1);
        }

        normal.Normalize();
        return normal;
    }

    private static Point3D Centroid(List<Point3D> points)
    {
        double x = 0;
        double y = 0;
        double z = 0;
        foreach (Point3D point in points)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
        }

        return new Point3D(x / points.Count, y / points.Count, z / points.Count);
    }

    private static double AngleOnCircle(Vector3D vector, Vector3D xDirection, Vector3D yDirection) =>
        Math.Atan2(Vector3D.DotProduct(vector, yDirection), Vector3D.DotProduct(vector, xDirection));

    private static double ToDouble(Parameter parameter) => parameter switch
    {
        Parameter.RealValue realValue => realValue.Value,
        Parameter.IntegerValue integerValue => integerValue.Value,
        _ => 0.0
    };

    private static List<Point3D> Compact(Point3D?[] points) =>
        points.Where(point => point.HasValue).Select(point => point!.Value).ToList();

    private static bool IsNamed(EntityInstance entity, string name) =>
        string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase);
}
