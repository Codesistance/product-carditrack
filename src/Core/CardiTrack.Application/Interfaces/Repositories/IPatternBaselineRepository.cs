using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IPatternBaselineRepository : IRepository<PatternBaseline>
{
    Task<PatternBaseline?> GetLatestByCardiMemberAsync(Guid cardiMemberId, int periodDays);

    /// <summary>
    /// The newest baseline for that window calculated no later than <paramref name="asOfUtc"/>.
    /// </summary>
    /// <remarks>
    /// For documents about a period in the past. The unbounded overload returns what the member's
    /// usual is <em>now</em>, which for a report covering last March would compare that March
    /// against months of subsequent knowledge and present it as what was normal at the time.
    /// </remarks>
    Task<PatternBaseline?> GetAsOfByCardiMemberAsync(Guid cardiMemberId, int periodDays, DateTime asOfUtc);
}
