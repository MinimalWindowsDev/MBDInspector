using System.Collections.Generic;
using StepParser.Parser;
using Xunit;

namespace MBDInspector.Tests;

public sealed class StepDocumentBuilderTests
{
    [Fact]
    public void ExtractLengthUnit_ReturnsMm_ForMillimetreFile()
    {
        IReadOnlyDictionary<int, EntityInstance> data = new Dictionary<int, EntityInstance>
        {
            [1] = new(
                1,
                null,
                [],
                [
                    new EntityComponent("LENGTH_UNIT", []),
                    new EntityComponent("NAMED_UNIT", [new Parameter.InheritedValue()]),
                    new EntityComponent("SI_UNIT", [new Parameter.EnumValue("MILLI"), new Parameter.EnumValue("METRE")])
                ])
        };

        string unit = StepDocumentBuilder.ExtractLengthUnit(data);

        Assert.Equal("mm", unit);
    }

    [Fact]
    public void ExtractPmi_FindsDimensionalSize()
    {
        IReadOnlyDictionary<int, EntityInstance> data = new Dictionary<int, EntityInstance>
        {
            [1] = new(1, "DIMENSIONAL_SIZE", [new Parameter.StringValue("WIDTH"), new Parameter.EntityReference(2)], null),
            [2] = new(2, "MEASURE_WITH_UNIT", [new Parameter.RealValue(12.5), new Parameter.EntityReference(3)], null),
            [3] = new(3, "SI_UNIT", [new Parameter.EnumValue("MILLI"), new Parameter.EnumValue("METRE")], null)
        };

        List<PmiDetail> details = StepDocumentBuilder.ExtractPmiDetails(data);

        Assert.Single(details);
        Assert.Equal("DIMENSIONAL_SIZE", details[0].Type);
        Assert.False(string.IsNullOrWhiteSpace(details[0].Value));
    }
}
