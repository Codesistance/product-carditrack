using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.IntegrationTests.Validators;

public class HistoryRepullValidatorTests
{
    private readonly HistoryRepullValidator _sut = new();

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(45)]
    [InlineData(90)]
    public void Accepts_DaysInsideTheRange(int days)
    {
        Assert.True(_sut.Validate(new HistoryRepullRequest { Days = days }).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(91)]
    public void Rejects_DaysOutsideTheRange(int days)
    {
        var result = _sut.Validate(new HistoryRepullRequest { Days = days });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(HistoryRepullRequest.Days));
    }
}
