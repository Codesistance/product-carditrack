using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The canned answers offered on an alert, and the server-side check that a stored code is one of
/// them.
/// </summary>
/// <remarks>
/// The invariants here are cheap to hold and expensive to lose. A rule with no chips makes
/// answering a typing job, which is how a family stops answering; a code the catalogue does not
/// know is a stored value nothing can render a label for.
/// </remarks>
public class AlertResponseCatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a_rule_that_does_not_exist")]
    [InlineData("custom:3f2a1c94-0000-0000-0000-000000000000")]
    public void EveryRule_IncludingOnesTheCatalogueHasNeverHeardOf_GetsChipsForBothActions(string? rule)
    {
        // A caregiver's own alarm is the case that will actually happen: its wording is theirs,
        // and nothing here could anticipate it. Falling back beats offering nothing.
        Assert.NotEmpty(AlertResponseCatalog.For(rule, AlertResponseCatalog.ResponseKind.Acknowledge));
        Assert.NotEmpty(AlertResponseCatalog.For(rule, AlertResponseCatalog.ResponseKind.Close));
    }

    [Fact]
    public void ARulesOwnAnswers_AreOfferedInsteadOfTheGenericOnes()
    {
        var close = AlertResponseCatalog
            .CodesFor(AlertRuleCatalogue.DeviceSilence, AlertResponseCatalog.ResponseKind.Close);

        // The only rule whose alert is about the equipment rather than the person — "spoke to
        // them, they're fine" answers a question nobody asked about a flat battery.
        Assert.Contains("charged_and_worn", close);
        Assert.DoesNotContain("spoke_to_them", close);
    }

    [Fact]
    public void AcknowledgeAndCloseAreDifferentLists_BecauseTheyAnswerDifferentQuestions()
    {
        var rule = AlertRuleCatalogue.NoMorningActivity;

        var acknowledge = AlertResponseCatalog.CodesFor(rule, AlertResponseCatalog.ResponseKind.Acknowledge);
        var close = AlertResponseCatalog.CodesFor(rule, AlertResponseCatalog.ResponseKind.Close);

        // Acknowledging states an intention ("calling them now"); closing states an outcome. A
        // shared list would let somebody close an alert with "going to check in person", which
        // says the opposite of what closing means.
        Assert.Contains("calling", acknowledge);
        Assert.DoesNotContain("calling", close);
        Assert.Contains("awake_and_fine", close);
    }

    [Fact]
    public void ACodeFromAnotherRule_IsNotValidHere()
    {
        Assert.False(AlertResponseCatalog.IsValid(
            AlertRuleCatalogue.NoMorningActivity,
            AlertResponseCatalog.ResponseKind.Close,
            "charged_and_worn"));
    }

    [Fact]
    public void ACodeFromTheOtherActionOfTheSameRule_IsNotValidEither()
    {
        // The narrower mistake, and the likelier one: a client that sends its acknowledge list to
        // the close endpoint. Validating per rule but not per action would let it through.
        Assert.False(AlertResponseCatalog.IsValid(
            AlertRuleCatalogue.NoMorningActivity,
            AlertResponseCatalog.ResponseKind.Close,
            "calling"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCodeAtAll_IsValid_BecauseANoteOnItsOwnIsAnAnswer(string? code)
    {
        Assert.True(AlertResponseCatalog.IsValid(
            AlertRuleCatalogue.ActivityDecline, AlertResponseCatalog.ResponseKind.Close, code));
    }

    [Fact]
    public void EveryOfferedCode_ValidatesForTheListItCameFrom()
    {
        // The round trip the API depends on: whatever the detail response offered, the answer
        // endpoint must accept. These are two reads of one table today and must stay so.
        string?[] rules =
        [
            null,
            AlertRuleCatalogue.DeviceSilence,
            AlertRuleCatalogue.NoMorningActivity,
            AlertRuleCatalogue.IrregularSleep,
            AlertRuleCatalogue.ActivityDecline,
        ];

        foreach (var rule in rules)
        {
            foreach (var kind in Enum.GetValues<AlertResponseCatalog.ResponseKind>())
            {
                foreach (var option in AlertResponseCatalog.For(rule, kind))
                    Assert.True(AlertResponseCatalog.IsValid(rule, kind, option.Code),
                        $"{rule ?? "(generic)"}/{kind} offered {option.Code} but would refuse it");
            }
        }
    }

    [Fact]
    public void NoListOffersTheSameCodeTwice()
    {
        // A duplicate renders as two identical chips and stores one value — the sort of thing
        // nobody notices until a caregiver reports the sheet looking broken.
        string?[] rules =
        [
            null,
            AlertRuleCatalogue.DeviceSilence,
            AlertRuleCatalogue.NoMorningActivity,
            AlertRuleCatalogue.IrregularSleep,
            AlertRuleCatalogue.ActivityDecline,
        ];

        foreach (var rule in rules)
        {
            foreach (var kind in Enum.GetValues<AlertResponseCatalog.ResponseKind>())
            {
                var codes = AlertResponseCatalog.CodesFor(rule, kind);
                Assert.Equal(codes.Count, codes.Distinct().Count());
            }
        }
    }
}
