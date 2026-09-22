using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface IOrganizationService
{
    Task<OrganizationResponse?> GetByIdAsync(Guid id);
}
