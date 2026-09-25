using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Domain;

/// <summary>
/// The split rule is applied twice — once by the migration's SQL to every stored name, and again
/// to every legacy client's single <c>Name</c> — and the two must agree
/// (<c>SplitCardiMemberNameMigrationTests</c> proves the SQL side against this one).
/// </summary>
public class PersonNameTests
{
    [Theory]
    [InlineData("Arthur Doe", "Arthur", "Doe")]
    [InlineData("Arthur", "Arthur", null)]
    [InlineData("Mary Ann Smith", "Mary", "Ann Smith")]
    [InlineData("  Padded   Name  ", "Padded", "Name")]
    [InlineData("Tab\tSeparated", "Tab", "Separated")]
    [InlineData("Trailing ", "Trailing", null)]
    [InlineData("", "", null)]
    [InlineData("   ", "", null)]
    [InlineData(null, "", null)]
    public void Split_TakesTheFirstWordAsFirstNameAndTheRestAsLast(string? full, string first, string? last)
    {
        Assert.Equal((first, last), PersonName.Split(full));
    }

    [Theory]
    [InlineData("Arthur", "Doe", "Arthur Doe")]
    [InlineData("Arthur", null, "Arthur")]
    [InlineData("Arthur", "  ", "Arthur")]
    [InlineData("Mary Ann", "Smith", "Mary Ann Smith")]
    public void Join_PutsASingleSpaceBetween_AndOmitsAMissingLastName(string first, string? last, string full)
    {
        Assert.Equal(full, PersonName.Join(first, last));
    }

    [Fact]
    public void FullName_IsDerivedFromTheTwoParts_SoItCannotDriftFromThem()
    {
        var member = new CardiMember { FirstName = "Arthur", LastName = "Doe" };
        Assert.Equal("Arthur Doe", member.FullName);

        member.LastName = null;
        Assert.Equal("Arthur", member.FullName);
    }
}
