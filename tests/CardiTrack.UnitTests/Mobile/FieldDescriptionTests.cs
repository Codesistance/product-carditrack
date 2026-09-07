using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// What the choice and date fields tell a screen reader: the label first, then the value, and an
/// honest word for an empty field.
/// </summary>
public class FieldDescriptionTests
{
    [Fact]
    public void LabelAndValue_ReadAsLabelColonValue()
    {
        Assert.Equal("Date of birth: 6 Aug 1960", FieldDescription.For("Date of birth", "6 Aug 1960", "no date chosen"));
    }

    [Fact]
    public void AnEmptyField_SaysSoAfterItsLabel()
    {
        Assert.Equal("Date of birth: no date chosen", FieldDescription.For("Date of birth", null, "no date chosen"));
        Assert.Equal("Sex: not set", FieldDescription.For("Sex", "  ", "not set"));
    }

    [Fact]
    public void NoLabel_IsTheValueAlone()
    {
        Assert.Equal("Parent", FieldDescription.For(null, "Parent", "not set"));
        Assert.Equal("not set", FieldDescription.For(string.Empty, null, "not set"));
    }
}
