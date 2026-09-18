namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// A wearer-channel OAuth exchange that succeeded: the connection it produced, and the invitation
/// it was authorized by.
/// </summary>
/// <remarks>
/// The invite id travels back with the connection rather than being looked up again, because the
/// only authority for which invite this grant belongs to is the state token that has just been
/// consumed. Re-deriving it afterwards — by member and brand, say — would find whichever invite
/// happens to be live now, which is not necessarily the one the wearer actually followed.
/// </remarks>
/// <param name="Device">The stored connection, as the device list would describe it.</param>
/// <param name="InviteId">The invitation whose state token authorized this exchange.</param>
public sealed record WearerConnectionCompletion(DeviceResponse Device, Guid InviteId);
