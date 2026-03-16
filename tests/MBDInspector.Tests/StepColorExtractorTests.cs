using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StepParser.Parser;
using Xunit;

namespace MBDInspector.Tests;

public sealed class StepColorExtractorTests
{
    [Fact]
    public void Extract_PropagatesStyledSolidColor_ToReferencedFace()
    {
        IReadOnlyDictionary<int, EntityInstance> data = new Dictionary<int, EntityInstance>
        {
            [1] = new(1, "ADVANCED_FACE", [new Parameter.StringValue(""), new Parameter.ListValue([]), new Parameter.UnsetValue(), new Parameter.EnumValue("T")], null),
            [2] = new(2, "MANIFOLD_SOLID_BREP", [new Parameter.StringValue("solid"), new Parameter.EntityReference(1)], null),
            [3] = new(3, "STYLED_ITEM", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(4)]), new Parameter.EntityReference(2)], null),
            [4] = new(4, "PRESENTATION_STYLE_ASSIGNMENT", [new Parameter.ListValue([new Parameter.EntityReference(5)])], null),
            [5] = new(5, "SURFACE_STYLE_USAGE", [new Parameter.EnumValue("BOTH"), new Parameter.EntityReference(6)], null),
            [6] = new(6, "SURFACE_SIDE_STYLE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(7)])], null),
            [7] = new(7, "SURFACE_STYLE_FILL_AREA", [new Parameter.EntityReference(8)], null),
            [8] = new(8, "FILL_AREA_STYLE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(9)])], null),
            [9] = new(9, "FILL_AREA_STYLE_COLOUR", [new Parameter.StringValue(""), new Parameter.EntityReference(10)], null),
            [10] = new(10, "COLOUR_RGB", [new Parameter.StringValue(""), new Parameter.RealValue(1.0), new Parameter.RealValue(0.5), new Parameter.RealValue(0.0)], null)
        };

        Dictionary<int, Color> colors = StepColorExtractor.Extract(data);

        Assert.Equal(Color.FromRgb(255, 128, 0), colors[1]);
        Assert.Equal(Color.FromRgb(255, 128, 0), colors[2]);
    }

    [Fact]
    public void Extract_ResolvesPredefinedColorNames()
    {
        IReadOnlyDictionary<int, EntityInstance> data = new Dictionary<int, EntityInstance>
        {
            [1] = new(1, "ADVANCED_FACE", [new Parameter.StringValue(""), new Parameter.ListValue([]), new Parameter.UnsetValue(), new Parameter.EnumValue("T")], null),
            [3] = new(3, "STYLED_ITEM", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(4)]), new Parameter.EntityReference(1)], null),
            [4] = new(4, "PRESENTATION_STYLE_ASSIGNMENT", [new Parameter.ListValue([new Parameter.EntityReference(5)])], null),
            [5] = new(5, "SURFACE_STYLE_USAGE", [new Parameter.EnumValue("BOTH"), new Parameter.EntityReference(6)], null),
            [6] = new(6, "SURFACE_SIDE_STYLE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(7)])], null),
            [7] = new(7, "SURFACE_STYLE_FILL_AREA", [new Parameter.EntityReference(8)], null),
            [8] = new(8, "FILL_AREA_STYLE", [new Parameter.StringValue(""), new Parameter.ListValue([new Parameter.EntityReference(9)])], null),
            [9] = new(9, "FILL_AREA_STYLE_COLOUR", [new Parameter.StringValue(""), new Parameter.EntityReference(10)], null),
            [10] = new(10, "PRE_DEFINED_COLOUR", [new Parameter.StringValue("red")], null)
        };

        Dictionary<int, Color> colors = StepColorExtractor.Extract(data);

        Assert.Equal(Colors.Red, colors[1]);
    }

    [Fact]
    public void MakeMaterial_EmissiveFloor_IsNeverBlack()
    {
        Material material = MainWindow.MakeMaterial(Color.FromRgb(64, 64, 64), 1.0, 1.0);

        MaterialGroup group = Assert.IsType<MaterialGroup>(material);
        EmissiveMaterial emissive = Assert.IsType<EmissiveMaterial>(group.Children.Single(child => child is EmissiveMaterial));
        SolidColorBrush brush = Assert.IsType<SolidColorBrush>(emissive.Brush);

        Assert.True(brush.Color.R > 0);
        Assert.True(brush.Color.G > 0);
        Assert.True(brush.Color.B > 0);
    }
}
