using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media.Media3D;
using StepParser.Parser;
using Xunit;

namespace MBDInspector.Tests;

public sealed class StepTessellatorTests
{
    [Fact]
    public void TryTessellateFace_EmitsNormals_ForPlanarFace()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreatePlanarFaceData();

        bool success = StepTessellator.TryTessellateFace(100, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Positions);
        Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
        Assert.NotEmpty(mesh.TriangleIndices);
    }

    [Fact]
    public void TryTessellateFace_FreezesMesh_ForCrossThreadReuse()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreatePlanarFaceData();

        bool success = StepTessellator.TryTessellateFace(100, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.True(mesh!.IsFrozen);
    }

    [Fact]
    public void TryTessellateFace_SupportsTrimmedCurveWrapper()
    {
        Dictionary<int, EntityInstance> data = new(CreatePlanarFaceData())
        {
            [120] = new(120, "TRIMMED_CURVE",
                [new Parameter.StringValue(""), new Parameter.EntityReference(20), new Parameter.ListValue([]), new Parameter.ListValue([]), new Parameter.EnumValue("T"), new Parameter.EnumValue("PARAMETER")], null),
            [30] = EdgeCurve(30, 10, 11, 120)
        };

        bool success = StepTessellator.TryTessellateFace(100, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
    }

    [Fact]
    public void TryTessellateFace_SupportsConicalSurface()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateConicalFaceData();

        bool success = StepTessellator.TryTessellateFace(200, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
        Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
    }

    [Fact]
    public void TryTessellateFace_SupportsSphericalSurface()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateSphericalFaceData();

        bool success = StepTessellator.TryTessellateFace(300, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
        Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
    }

    [Fact]
    public void TryTessellateFace_SupportsComplexBsplineCurveWrapper()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateComplexBsplinePlanarFaceData();

        bool success = StepTessellator.TryTessellateFace(400, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
    }

    [Fact]
    public void TryTessellateFace_SupportsSimpleBsplineCurveWithKnots()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateSimpleBsplineWithKnotsPlanarFaceData();

        bool success = StepTessellator.TryTessellateFace(450, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
    }

    [Fact]
    public void TryTessellateFace_PrefersFaceOuterBoundOverInnerFaceBound()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreatePlanarFaceWithInnerBoundFirstData();

        bool success = StepTessellator.TryTessellateFace(500, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
    }

    [Fact]
    public void TryTessellateFace_SupportsSurfaceOfRevolution()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateSurfaceOfRevolutionFaceData();

        bool success = StepTessellator.TryTessellateFace(600, data, out MeshGeometry3D? mesh);

        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Positions);
        Assert.All(mesh.Normals, normal => Assert.InRange(normal.Length, 0.999, 1.001));
    }

    [Fact]
    public void TessellateFaces_IsOrderDeterministic()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateMultipleFaceData();

        List<int> first = StepTessellator.TessellateFaces(data).Select(item => item.EntityId).ToList();
        List<int> second = StepTessellator.TessellateFaces(data).Select(item => item.EntityId).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryTessellateFace_HandlesHighVertexCountPolygon()
    {
        IReadOnlyDictionary<int, EntityInstance> data = CreateHighVertexPlanarFaceData(200);
        Stopwatch stopwatch = Stopwatch.StartNew();

        bool success = StepTessellator.TryTessellateFace(900, data, out MeshGeometry3D? mesh);

        stopwatch.Stop();
        Assert.True(success);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.TriangleIndices);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Tessellation took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }
    private static IReadOnlyDictionary<int, EntityInstance> CreatePlanarFaceData() => new Dictionary<int, EntityInstance>
    {
        [1] = CartesianPoint(1, 0, 0, 0),
        [2] = CartesianPoint(2, 10, 0, 0),
        [3] = CartesianPoint(3, 10, 5, 0),
        [4] = CartesianPoint(4, 0, 5, 0),
        [10] = VertexPoint(10, 1),
        [11] = VertexPoint(11, 2),
        [12] = VertexPoint(12, 3),
        [13] = VertexPoint(13, 4),
        [20] = new(20, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(1), new Parameter.UnsetValue()], null),
        [21] = new(21, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(2), new Parameter.UnsetValue()], null),
        [22] = new(22, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(3), new Parameter.UnsetValue()], null),
        [23] = new(23, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(4), new Parameter.UnsetValue()], null),
        [30] = EdgeCurve(30, 10, 11, 20),
        [31] = EdgeCurve(31, 11, 12, 21),
        [32] = EdgeCurve(32, 12, 13, 22),
        [33] = EdgeCurve(33, 13, 10, 23),
        [40] = OrientedEdge(40, 30),
        [41] = OrientedEdge(41, 31),
        [42] = OrientedEdge(42, 32),
        [43] = OrientedEdge(43, 33),
        [50] = EdgeLoop(50, 40, 41, 42, 43),
        [60] = FaceOuterBound(60, 50),
        [70] = Direction(70, 0, 0, 1),
        [71] = Direction(71, 1, 0, 0),
        [72] = Axis2Placement(72, 1, 70, 71),
        [80] = new(80, "PLANE", [new Parameter.StringValue(""), new Parameter.EntityReference(72)], null),
        [100] = AdvancedFace(100, 60, 80)
    };

    private static IReadOnlyDictionary<int, EntityInstance> CreateConicalFaceData()
    {
        double r0 = 2.0;
        double semiAngle = 0.25;
        double z1 = 1.5;
        double r1 = r0 + (z1 * Math.Tan(semiAngle));
        return new Dictionary<int, EntityInstance>
        {
            [1] = CartesianPoint(1, r0, 0, 0),
            [2] = CartesianPoint(2, 0, r0, 0),
            [3] = CartesianPoint(3, 0, r1, z1),
            [4] = CartesianPoint(4, r1, 0, z1),
            [10] = VertexPoint(10, 1), [11] = VertexPoint(11, 2), [12] = VertexPoint(12, 3), [13] = VertexPoint(13, 4),
            [20] = new(20, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(1), new Parameter.UnsetValue()], null),
            [21] = new(21, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(2), new Parameter.UnsetValue()], null),
            [22] = new(22, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(3), new Parameter.UnsetValue()], null),
            [23] = new(23, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(4), new Parameter.UnsetValue()], null),
            [30] = EdgeCurve(30, 10, 11, 20), [31] = EdgeCurve(31, 11, 12, 21), [32] = EdgeCurve(32, 12, 13, 22), [33] = EdgeCurve(33, 13, 10, 23),
            [40] = OrientedEdge(40, 30), [41] = OrientedEdge(41, 31), [42] = OrientedEdge(42, 32), [43] = OrientedEdge(43, 33),
            [50] = EdgeLoop(50, 40, 41, 42, 43),
            [60] = FaceOuterBound(60, 50),
            [70] = Direction(70, 0, 0, 1), [71] = Direction(71, 1, 0, 0),
            [72] = Axis2Placement(72, 1, 70, 71),
            [80] = new(80, "CONICAL_SURFACE", [new Parameter.StringValue(""), new Parameter.EntityReference(72), new Parameter.RealValue(r0), new Parameter.RealValue(semiAngle)], null),
            [200] = AdvancedFace(200, 60, 80)
        };
    }

    private static IReadOnlyDictionary<int, EntityInstance> CreateSphericalFaceData()
    {
        double r = 3.0;
        Point3D P(double lonDeg, double latDeg)
        {
            double lon = lonDeg * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;
            double cosLat = Math.Cos(lat);
            return new Point3D(r * cosLat * Math.Cos(lon), r * cosLat * Math.Sin(lon), r * Math.Sin(lat));
        }

        Point3D p1 = P(0, 0); Point3D p2 = P(45, 0); Point3D p3 = P(45, 30); Point3D p4 = P(0, 30);
        return new Dictionary<int, EntityInstance>
        {
            [1] = CartesianPoint(1, p1.X, p1.Y, p1.Z), [2] = CartesianPoint(2, p2.X, p2.Y, p2.Z),
            [3] = CartesianPoint(3, p3.X, p3.Y, p3.Z), [4] = CartesianPoint(4, p4.X, p4.Y, p4.Z),
            [10] = VertexPoint(10, 1), [11] = VertexPoint(11, 2), [12] = VertexPoint(12, 3), [13] = VertexPoint(13, 4),
            [20] = new(20, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(1), new Parameter.UnsetValue()], null),
            [21] = new(21, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(2), new Parameter.UnsetValue()], null),
            [22] = new(22, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(3), new Parameter.UnsetValue()], null),
            [23] = new(23, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(4), new Parameter.UnsetValue()], null),
            [30] = EdgeCurve(30, 10, 11, 20), [31] = EdgeCurve(31, 11, 12, 21), [32] = EdgeCurve(32, 12, 13, 22), [33] = EdgeCurve(33, 13, 10, 23),
            [40] = OrientedEdge(40, 30), [41] = OrientedEdge(41, 31), [42] = OrientedEdge(42, 32), [43] = OrientedEdge(43, 33),
            [50] = EdgeLoop(50, 40, 41, 42, 43),
            [60] = FaceOuterBound(60, 50),
            [70] = Direction(70, 0, 0, 1), [71] = Direction(71, 1, 0, 0),
            [72] = Axis2Placement(72, 1, 70, 71),
            [80] = new(80, "SPHERICAL_SURFACE", [new Parameter.StringValue(""), new Parameter.EntityReference(72), new Parameter.RealValue(r)], null),
            [300] = AdvancedFace(300, 60, 80)
        };
    }

    private static IReadOnlyDictionary<int, EntityInstance> CreateComplexBsplinePlanarFaceData() => new Dictionary<int, EntityInstance>
    {
        [1] = CartesianPoint(1, 0, 0, 0),
        [2] = CartesianPoint(2, 10, 0, 0),
        [3] = CartesianPoint(3, 10, 5, 0),
        [4] = CartesianPoint(4, 0, 5, 0),
        [10] = VertexPoint(10, 1),
        [11] = VertexPoint(11, 2),
        [12] = VertexPoint(12, 3),
        [13] = VertexPoint(13, 4),
        [20] = ComplexBsplineCurve(20, 1, 2),
        [21] = ComplexBsplineCurve(21, 2, 3),
        [22] = ComplexBsplineCurve(22, 3, 4),
        [23] = ComplexBsplineCurve(23, 4, 1),
        [30] = EdgeCurve(30, 10, 11, 20),
        [31] = EdgeCurve(31, 11, 12, 21),
        [32] = EdgeCurve(32, 12, 13, 22),
        [33] = EdgeCurve(33, 13, 10, 23),
        [40] = OrientedEdge(40, 30),
        [41] = OrientedEdge(41, 31),
        [42] = OrientedEdge(42, 32),
        [43] = OrientedEdge(43, 33),
        [50] = EdgeLoop(50, 40, 41, 42, 43),
        [60] = FaceOuterBound(60, 50),
        [70] = Direction(70, 0, 0, 1),
        [71] = Direction(71, 1, 0, 0),
        [72] = Axis2Placement(72, 1, 70, 71),
        [80] = new(80, "PLANE", [new Parameter.StringValue(""), new Parameter.EntityReference(72)], null),
        [400] = AdvancedFace(400, 60, 80)
    };

    private static IReadOnlyDictionary<int, EntityInstance> CreateSimpleBsplineWithKnotsPlanarFaceData() => new Dictionary<int, EntityInstance>
    {
        [1] = CartesianPoint(1, 0, 0, 0),
        [2] = CartesianPoint(2, 10, 0, 0),
        [3] = CartesianPoint(3, 10, 5, 0),
        [4] = CartesianPoint(4, 0, 5, 0),
        [10] = VertexPoint(10, 1),
        [11] = VertexPoint(11, 2),
        [12] = VertexPoint(12, 3),
        [13] = VertexPoint(13, 4),
        [20] = SimpleBsplineWithKnots(20, 1, 2),
        [21] = SimpleBsplineWithKnots(21, 2, 3),
        [22] = SimpleBsplineWithKnots(22, 3, 4),
        [23] = SimpleBsplineWithKnots(23, 4, 1),
        [30] = EdgeCurve(30, 10, 11, 20),
        [31] = EdgeCurve(31, 11, 12, 21),
        [32] = EdgeCurve(32, 12, 13, 22),
        [33] = EdgeCurve(33, 13, 10, 23),
        [40] = OrientedEdge(40, 30),
        [41] = OrientedEdge(41, 31),
        [42] = OrientedEdge(42, 32),
        [43] = OrientedEdge(43, 33),
        [50] = EdgeLoop(50, 40, 41, 42, 43),
        [60] = FaceOuterBound(60, 50),
        [70] = Direction(70, 0, 0, 1),
        [71] = Direction(71, 1, 0, 0),
        [72] = Axis2Placement(72, 1, 70, 71),
        [80] = new(80, "PLANE", [new Parameter.StringValue(""), new Parameter.EntityReference(72)], null),
        [450] = AdvancedFace(450, 60, 80)
    };

    private static IReadOnlyDictionary<int, EntityInstance> CreatePlanarFaceWithInnerBoundFirstData() => new Dictionary<int, EntityInstance>
    {
        [1] = CartesianPoint(1, 0, 0, 0),
        [2] = CartesianPoint(2, 10, 0, 0),
        [3] = CartesianPoint(3, 10, 5, 0),
        [4] = CartesianPoint(4, 0, 5, 0),
        [5] = CartesianPoint(5, 3, 2, 0),
        [6] = CartesianPoint(6, 7, 2, 0),
        [10] = VertexPoint(10, 1),
        [11] = VertexPoint(11, 2),
        [12] = VertexPoint(12, 3),
        [13] = VertexPoint(13, 4),
        [14] = VertexPoint(14, 5),
        [15] = VertexPoint(15, 6),
        [20] = new(20, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(1), new Parameter.UnsetValue()], null),
        [21] = new(21, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(2), new Parameter.UnsetValue()], null),
        [22] = new(22, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(3), new Parameter.UnsetValue()], null),
        [23] = new(23, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(4), new Parameter.UnsetValue()], null),
        [24] = new(24, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(5), new Parameter.UnsetValue()], null),
        [25] = new(25, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(6), new Parameter.UnsetValue()], null),
        [30] = EdgeCurve(30, 10, 11, 20),
        [31] = EdgeCurve(31, 11, 12, 21),
        [32] = EdgeCurve(32, 12, 13, 22),
        [33] = EdgeCurve(33, 13, 10, 23),
        [34] = EdgeCurve(34, 14, 15, 24),
        [35] = EdgeCurve(35, 15, 14, 25),
        [40] = OrientedEdge(40, 30),
        [41] = OrientedEdge(41, 31),
        [42] = OrientedEdge(42, 32),
        [43] = OrientedEdge(43, 33),
        [44] = OrientedEdge(44, 34),
        [45] = OrientedEdge(45, 35),
        [50] = EdgeLoop(50, 40, 41, 42, 43),
        [51] = EdgeLoop(51, 44, 45),
        [60] = FaceOuterBound(60, 50),
        [61] = new EntityInstance(61, "FACE_BOUND", [new Parameter.StringValue(""), new Parameter.EntityReference(51), new Parameter.EnumValue("T")], null),
        [70] = Direction(70, 0, 0, 1),
        [71] = Direction(71, 1, 0, 0),
        [72] = Axis2Placement(72, 1, 70, 71),
        [80] = new(80, "PLANE", [new Parameter.StringValue(""), new Parameter.EntityReference(72)], null),
        [500] = new EntityInstance(500, "ADVANCED_FACE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(61), new Parameter.EntityReference(60)]), new Parameter.EntityReference(80), new Parameter.EnumValue("T")], null)
    };

    private static EntityInstance CartesianPoint(int id, double x, double y, double z) =>
        new(id, "CARTESIAN_POINT", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.RealValue(x), new Parameter.RealValue(y), new Parameter.RealValue(z)])], null);

    private static EntityInstance VertexPoint(int id, int pointId) =>
        new(id, "VERTEX_POINT", [new Parameter.StringValue(""), new Parameter.EntityReference(pointId)], null);

    private static EntityInstance EdgeCurve(int id, int startId, int endId, int geometryId) =>
        new(id, "EDGE_CURVE", [new Parameter.StringValue(""), new Parameter.EntityReference(startId), new Parameter.EntityReference(endId), new Parameter.EntityReference(geometryId)], null);

    private static EntityInstance OrientedEdge(int id, int edgeCurveId) =>
        new(id, "ORIENTED_EDGE", [new Parameter.StringValue(""), new Parameter.UnsetValue(), new Parameter.UnsetValue(), new Parameter.EntityReference(edgeCurveId), new Parameter.EnumValue("T")], null);

    private static EntityInstance EdgeLoop(int id, params int[] orientedEdgeIds)
    {
        var items = new List<Parameter>();
        foreach (int edgeId in orientedEdgeIds)
        {
            items.Add(new Parameter.EntityReference(edgeId));
        }
        return new EntityInstance(id, "EDGE_LOOP", [new Parameter.StringValue(""), new Parameter.ListValue(items)], null);
    }

    private static EntityInstance FaceOuterBound(int id, int loopId) =>
        new(id, "FACE_OUTER_BOUND", [new Parameter.StringValue(""), new Parameter.EntityReference(loopId), new Parameter.EnumValue("T")], null);

    private static EntityInstance Direction(int id, double x, double y, double z) =>
        new(id, "DIRECTION", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.RealValue(x), new Parameter.RealValue(y), new Parameter.RealValue(z)])], null);

    private static EntityInstance Axis2Placement(int id, int pointId, int axisId, int refDirId) =>
        new(id, "AXIS2_PLACEMENT_3D", [new Parameter.StringValue(""), new Parameter.EntityReference(pointId), new Parameter.EntityReference(axisId), new Parameter.EntityReference(refDirId)], null);

    private static EntityInstance AdvancedFace(int id, int boundId, int surfaceId) =>
        new(id, "ADVANCED_FACE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(boundId)]), new Parameter.EntityReference(surfaceId), new Parameter.EnumValue("T")], null);

    private static EntityInstance ComplexBsplineCurve(int id, int startPointId, int endPointId) =>
        new(
            id,
            null,
            [],
            [
                new EntityComponent("BOUNDED_CURVE", []),
                new EntityComponent("B_SPLINE_CURVE", [
                    new Parameter.IntegerValue(3),
                    new Parameter.ListValue([
                        new Parameter.EntityReference(startPointId),
                        new Parameter.EntityReference(startPointId),
                        new Parameter.EntityReference(endPointId),
                        new Parameter.EntityReference(endPointId)
                    ]),
                    new Parameter.EnumValue("UNSPECIFIED"),
                    new Parameter.EnumValue("F"),
                    new Parameter.EnumValue("F")
                ])
            ]);

    private static EntityInstance SimpleBsplineWithKnots(int id, int startPointId, int endPointId) =>
        new(
            id,
            "B_SPLINE_CURVE_WITH_KNOTS",
            [
                new Parameter.StringValue(""),
                new Parameter.IntegerValue(3),
                new Parameter.ListValue([
                    new Parameter.EntityReference(startPointId),
                    new Parameter.EntityReference(startPointId),
                    new Parameter.EntityReference(endPointId),
                    new Parameter.EntityReference(endPointId)
                ]),
                new Parameter.EnumValue("UNSPECIFIED"),
                new Parameter.EnumValue("F"),
                new Parameter.EnumValue("F"),
                new Parameter.ListValue([new Parameter.IntegerValue(4), new Parameter.IntegerValue(4)]),
                new Parameter.ListValue([new Parameter.RealValue(0), new Parameter.RealValue(1)]),
                new Parameter.EnumValue("UNSPECIFIED")
            ],
            null);
    private static IReadOnlyDictionary<int, EntityInstance> CreateSurfaceOfRevolutionFaceData() => new Dictionary<int, EntityInstance>
    {
        [1] = CartesianPoint(1, 2, 0, 0),
        [2] = CartesianPoint(2, 2, 0, 4),
        [3] = CartesianPoint(3, 0, 2, 4),
        [4] = CartesianPoint(4, 0, 2, 0),
        [10] = VertexPoint(10, 1),
        [11] = VertexPoint(11, 2),
        [12] = VertexPoint(12, 3),
        [13] = VertexPoint(13, 4),
        [20] = new(20, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(1), new Parameter.UnsetValue()], null),
        [21] = new(21, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(2), new Parameter.UnsetValue()], null),
        [22] = new(22, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(3), new Parameter.UnsetValue()], null),
        [23] = new(23, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(4), new Parameter.UnsetValue()], null),
        [24] = new(24, "POLY_LINE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(1), new Parameter.EntityReference(2)])], null),
        [30] = EdgeCurve(30, 10, 11, 20),
        [31] = EdgeCurve(31, 11, 12, 21),
        [32] = EdgeCurve(32, 12, 13, 22),
        [33] = EdgeCurve(33, 13, 10, 23),
        [40] = OrientedEdge(40, 30),
        [41] = OrientedEdge(41, 31),
        [42] = OrientedEdge(42, 32),
        [43] = OrientedEdge(43, 33),
        [50] = EdgeLoop(50, 40, 41, 42, 43),
        [60] = FaceOuterBound(60, 50),
        [70] = Direction(70, 0, 0, 1),
        [71] = Direction(71, 1, 0, 0),
        [72] = Axis2Placement(72, 1, 70, 71),
        [73] = new(73, "SURFACE_OF_REVOLUTION", [new Parameter.StringValue(""), new Parameter.EntityReference(72), new Parameter.EntityReference(24)], null),
        [600] = AdvancedFace(600, 60, 73)
    };
    private static IReadOnlyDictionary<int, EntityInstance> CreateMultipleFaceData()
    {
        var result = new Dictionary<int, EntityInstance>();
        foreach (KeyValuePair<int, EntityInstance> pair in CreatePlanarFaceData())
        {
            result[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<int, EntityInstance> pair in CreateSurfaceOfRevolutionFaceData())
        {
            result[pair.Key + 1000] = OffsetEntity(pair.Value, 1000);
        }

        return result;
    }

    private static IReadOnlyDictionary<int, EntityInstance> CreateHighVertexPlanarFaceData(int vertexCount)
    {
        var data = new Dictionary<int, EntityInstance>();
        for (int index = 0; index < vertexCount; index++)
        {
            double angle = (2.0 * Math.PI * index) / vertexCount;
            int pointId = index + 1;
            int vertexId = 1000 + index;
            data[pointId] = CartesianPoint(pointId, Math.Cos(angle) * 10.0, Math.Sin(angle) * 10.0, 0.0);
            data[vertexId] = VertexPoint(vertexId, pointId);
        }

        var loopReferences = new List<Parameter>();
        for (int index = 0; index < vertexCount; index++)
        {
            int startPointId = index + 1;
            int endPointId = ((index + 1) % vertexCount) + 1;
            int startVertexId = 1000 + index;
            int endVertexId = 1000 + ((index + 1) % vertexCount);
            int lineId = 2000 + index;
            int edgeCurveId = 3000 + index;
            int orientedEdgeId = 4000 + index;

            data[lineId] = new EntityInstance(lineId, "LINE", [new Parameter.StringValue(""), new Parameter.EntityReference(startPointId), new Parameter.UnsetValue()], null);
            data[edgeCurveId] = EdgeCurve(edgeCurveId, startVertexId, endVertexId, lineId);
            data[orientedEdgeId] = OrientedEdge(orientedEdgeId, edgeCurveId);
            loopReferences.Add(new Parameter.EntityReference(orientedEdgeId));
        }

        data[8000] = new EntityInstance(8000, "EDGE_LOOP", [new Parameter.StringValue(""), new Parameter.ListValue(loopReferences)], null);
        data[8001] = FaceOuterBound(8001, 8000);
        data[8002] = Direction(8002, 0, 0, 1);
        data[8003] = Direction(8003, 1, 0, 0);
        data[8004] = Axis2Placement(8004, 1, 8002, 8003);
        data[8005] = new EntityInstance(8005, "PLANE", [new Parameter.StringValue(""), new Parameter.EntityReference(8004)], null);
        data[900] = AdvancedFace(900, 8001, 8005);
        return data;
    }

    private static EntityInstance OffsetEntity(EntityInstance entity, int offset)
    {
        IReadOnlyList<Parameter> parameters = entity.Parameters.Select(parameter => OffsetParameter(parameter, offset)).ToList();
        IReadOnlyList<EntityComponent>? components = entity.Components?
            .Select(component => new EntityComponent(component.Name, component.Parameters.Select(parameter => OffsetParameter(parameter, offset)).ToList()))
            .ToList();
        return new EntityInstance(entity.Id + offset, entity.Name, parameters, components);
    }

    private static Parameter OffsetParameter(Parameter parameter, int offset) => parameter switch
    {
        Parameter.EntityReference entityReference => new Parameter.EntityReference(entityReference.Id + offset),
        Parameter.ListValue listValue => new Parameter.ListValue(listValue.Items.Select(item => OffsetParameter(item, offset)).ToList()),
        Parameter.TypedValue typedValue => new Parameter.TypedValue(typedValue.TypeName, OffsetParameter(typedValue.Inner, offset)),
        _ => parameter
    };
}



