using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

public class UserCardiMember : BaseEntity, ISoftDeletable
{
    public Guid UserId { get; set; }
    public Guid CardiMemberId { get; set; }
    public RelationshipType RelationshipType { get; set; }
    public bool IsPrimaryCaregiver { get; set; }
    public bool CanViewHealthData { get; set; } = true;
    public bool ReceiveAlerts { get; set; } = true;

    public DateTime AssignedDate { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation properties
    public User? User { get; set; }
    public CardiMember? CardiMember { get; set; }

    public UserCardiMember()
    {
        AssignedDate = DateTime.UtcNow;
    }
}
