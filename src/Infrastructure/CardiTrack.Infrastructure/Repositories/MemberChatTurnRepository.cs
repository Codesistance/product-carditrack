using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberChatTurnRepository : Repository<MemberChatTurn>, IMemberChatTurnRepository
{
    public MemberChatTurnRepository(CardiTrackDbContext context) : base(context)
    {
    }
}
