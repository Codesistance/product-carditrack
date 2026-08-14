namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// A page of results plus enough to know whether there is another one — the shared envelope for
/// any list a client should fetch incrementally rather than all at once.
/// </summary>
public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
}
