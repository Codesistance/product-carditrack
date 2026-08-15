using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class AlertRuleOverridesTests
{
    [Fact]
    public void MissingOrEmptyJson_MeansAllEnabled()
    {
        Assert.True(AlertRuleOverrides.FromJson(null).IsEnabled(AlertRuleCatalogue.ActivityDecline));
        Assert.True(AlertRuleOverrides.FromJson("[]").IsEnabled(AlertRuleCatalogue.LateBedtime));
        Assert.True(AlertRuleOverrides.AllEnabled.IsEnabled(AlertRuleCatalogue.DeviceSilence));
    }

    [Fact]
    public void DisabledId_IsOff_OthersStayOn()
    {
        var prefs = AlertRuleOverrides.FromJson("""["activity_decline","late_bedtime"]""");
        Assert.False(prefs.IsEnabled(AlertRuleCatalogue.ActivityDecline));
        Assert.False(prefs.IsEnabled(AlertRuleCatalogue.LateBedtime));
        Assert.True(prefs.IsEnabled(AlertRuleCatalogue.IrregularSleep));
    }

    [Fact]
    public void UnknownIds_AreIgnored()
    {
        var prefs = AlertRuleOverrides.FromJson("""["not_a_real_rule","activity_decline"]""");
        Assert.False(prefs.IsEnabled(AlertRuleCatalogue.ActivityDecline));
        Assert.DoesNotContain("not_a_real_rule", prefs.DisabledRuleIds);
    }

    [Fact]
    public void CorruptJson_FailsOpenToAllOn()
    {
        Assert.True(AlertRuleOverrides.FromJson("{not-json").IsEnabled(AlertRuleCatalogue.NoMorningActivity));
    }

    [Fact]
    public void WithEnabled_RoundTripsThroughJson()
    {
        var off = AlertRuleOverrides.AllEnabled.WithEnabled(AlertRuleCatalogue.IrregularSleep, enabled: false);
        var parsed = AlertRuleOverrides.FromJson(off.ToJson());
        Assert.False(parsed.IsEnabled(AlertRuleCatalogue.IrregularSleep));
        Assert.True(parsed.WithEnabled(AlertRuleCatalogue.IrregularSleep, enabled: true)
            .IsEnabled(AlertRuleCatalogue.IrregularSleep));
        Assert.Equal("[]", parsed.WithEnabled(AlertRuleCatalogue.IrregularSleep, enabled: true).ToJson());
    }
}

public class AlertPreferenceServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAlertPreferenceRepository _prefs = Substitute.For<IAlertPreferenceRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public AlertPreferenceServiceTests()
    {
        _unitOfWork.AlertPreferences.Returns(_prefs);
        _unitOfWork.CardiMembers.Returns(_members);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret",
            DateOfBirth = new DateOnly(1948, 3, 2),
            IsActive = true,
        });
    }

    private AlertPreferenceService CreateSut() => new(_unitOfWork, _access);

    [Fact]
    public async Task Get_WithNoRow_ReturnsEveryRuleEnabled()
    {
        _prefs.GetByCardiMemberIdAsync(_memberId).Returns((AlertPreference?)null);

        var response = await CreateSut().GetAsync(_userId, _memberId);

        Assert.Equal(_memberId, response.CardiMemberId);
        Assert.NotEmpty(response.Clusters);
        Assert.All(response.Clusters.SelectMany(c => c.Rules), r => Assert.True(r.Enabled));
        await _access.Received(1).RequireViewAccessAsync(_userId, _memberId);
    }

    [Fact]
    public async Task SetRuleEnabled_Off_PersistsSparseDisableList()
    {
        _prefs.GetByCardiMemberIdAsync(_memberId).Returns((AlertPreference?)null);
        AlertPreference? saved = null;
        await _prefs.AddAsync(Arg.Do<AlertPreference>(p => saved = p));

        var updated = await CreateSut().SetRuleEnabledAsync(
            _userId, _memberId, AlertRuleCatalogue.ActivityDecline, enabled: false);

        Assert.False(updated.Enabled);
        Assert.NotNull(saved);
        Assert.Equal(_memberId, saved!.CardiMemberId);
        Assert.Contains(AlertRuleCatalogue.ActivityDecline, saved.DisabledRules);
        await _unitOfWork.Received(1).SaveChangesAsync();
        await _access.Received(1).RequireManageAccessAsync(_userId, _memberId);
    }

    [Fact]
    public async Task SetRuleEnabled_BackOn_RemovesRowWhenNothingLeftDisabled()
    {
        var existing = new AlertPreference
        {
            CardiMemberId = _memberId,
            DisabledRules = """["activity_decline"]""",
        };
        _prefs.GetByCardiMemberIdAsync(_memberId).Returns(existing);

        await CreateSut().SetRuleEnabledAsync(
            _userId, _memberId, AlertRuleCatalogue.ActivityDecline, enabled: true);

        _prefs.Received(1).Remove(existing);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task SetRuleEnabled_UnknownOrUnimplemented_Rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateSut().SetRuleEnabledAsync(_userId, _memberId, "not_a_rule", false));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().SetRuleEnabledAsync(_userId, _memberId, AlertRuleCatalogue.LateBedtime, false));
    }
}
