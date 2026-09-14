using System.Security.Cryptography;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

public class CardiMemberService : ICardiMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly IEncryptionService _encryption;
    private readonly INotificationGapResolver _gapResolver;
    private readonly IProfilePhotoProcessor _photoProcessor;
    private readonly IProfilePhotoStorage _photoStorage;

    public CardiMemberService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IEncryptionService encryption,
        INotificationGapResolver gapResolver,
        IProfilePhotoProcessor photoProcessor,
        IProfilePhotoStorage photoStorage)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _encryption = encryption;
        _gapResolver = gapResolver;
        _photoProcessor = photoProcessor;
        _photoStorage = photoStorage;
    }

    /// <summary>
    /// The photo is stored before the member row exists (a refused photo must not half-create a
    /// member), so a creation that fails after the upload would leave health imagery in the
    /// bucket that no row points at. Delete it here; the caller's exception is the one that
    /// matters, so a failed delete is not allowed to replace it — <c>OrphanedPhotoCleanupWorker</c>
    /// sweeps whatever this misses.
    /// </summary>
    private async Task DiscardUploadedPhotoAsync(CardiMember cardiMember)
    {
        if (string.IsNullOrEmpty(cardiMember.PhotoObjectName))
            return;

        try
        {
            await _photoStorage.DeleteAsync(cardiMember.PhotoObjectName);
        }
        catch
        {
            // Swallowed on purpose: see the summary. The original failure is being rethrown.
        }
    }

    /// <summary>
    /// How long a spent creation key is kept. Trimming, not expiry: nothing refuses an older key,
    /// and <see cref="ICardiMemberCreationKeyRepository.FindAsync"/> answers whatever is still
    /// there.
    /// </summary>
    /// <remarks>
    /// Seven days, not the one this started at, because the mobile form now persists its key with
    /// the draft and <c>CardiMemberDraftStore.Lifetime</c> is seven days. A shorter window here
    /// would delete the key of a draft the caregiver can still open and submit — which is exactly
    /// the retry the key exists for, and it would fail silently by creating a second member. The
    /// two numbers are coupled: shorten this and the draft store's protection goes with it.
    /// Deliberately restated rather than shared, because <c>Application</c> cannot reference
    /// <c>Mobile.Core</c> and should not learn about a client's storage to find out.
    /// </remarks>
    private static readonly TimeSpan CreationKeyLifetime = TimeSpan.FromDays(7);

    public async Task<CardiMemberResponse> CreateCardiMemberAsync(
        Guid organizationId,
        Guid userId,
        CreateCardiMemberRequest request,
        string? idempotencyKey = null)
    {
        // The retry's question, asked before any work: has this caregiver already made this
        // attempt? A hit means the earlier transaction committed — the key row could not exist
        // otherwise — so the member it names is the one they meant to create, whatever the
        // response they actually saw.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var already = await _unitOfWork.CardiMemberCreationKeys.FindAsync(userId, idempotencyKey);
            if (already is not null)
            {
                if (await ReusableMemberAsync(userId, already.CardiMemberId) is { } theirs)
                    return theirs;

                // The attempt landed and the member has since been removed. Removal is a soft
                // delete — the row stays with IsActive false and its links deactivated — so
                // "is it still there" is the wrong question: handing that row back would return a
                // member this caregiver can no longer reach, with their profile data, and would
                // silently skip the re-add they asked for. Retrying after a removal is a request
                // to add that person again, so clear the spent key and create, rather than
                // leaving it to collide on the unique index and fail with a 500 nobody can act on.
                _unitOfWork.CardiMemberCreationKeys.Remove(already);
                await _unitOfWork.SaveChangesAsync();
            }
        }

        var cardiMember = new CardiMember
        {
            OrganizationId = organizationId,
            Name = request.Name,
            DateOfBirth = request.DateOfBirth,
            Gender = request.Gender,
            Email = request.Email,
            Phone = request.Phone,
            EmergencyContactName = request.EmergencyContactName,
            EmergencyContactPhone = request.EmergencyContactPhone,
            MedicalNotes = Protect(request.MedicalNotes),
            IsActive = true
        };

        // Photo work happens BEFORE the insert: a refused or unstorable photo must not leave a
        // member half-created — the caregiver would have no idea which parts of the form stuck.
        if (!string.IsNullOrWhiteSpace(request.PhotoBase64))
        {
            var jpeg = ProcessPhotoOrThrow(request.PhotoBase64);
            cardiMember.PhotoObjectName = await _photoStorage.UploadAsync(cardiMember.Id, jpeg);
        }

        // The member and the caregiver's link to it are one creation. Two saves without a
        // transaction could leave a member row that no caregiver is linked to — reachable by
        // nobody, deletable by nobody, and named by no audit entry (the id is handed to the
        // audit middleware only once this method returns). The transaction starts inside the
        // try so that a failure to start it still discards the photo uploaded above; rolling
        // back with no transaction open is a no-op.
        //
        // Once Commit has been *attempted* the outcome is indeterminate — the server can accept
        // the commit and the call still fail on the way back. A member may then exist and own
        // the photo, so the object is left alone on that path: OrphanedPhotoCleanupWorker deletes
        // it only if no row ever points at it.
        var commitAttempted = false;
        try
        {
            await _unitOfWork.BeginTransactionAsync();

            await _unitOfWork.CardiMembers.AddAsync(cardiMember);
            await _unitOfWork.SaveChangesAsync(); // Save to get ID

            // Create relationship between user and CardiMember
            var userCardiMember = new UserCardiMember
            {
                UserId = userId,
                CardiMemberId = cardiMember.Id,
                RelationshipType = Stated(request.RelationshipType),
                IsPrimaryCaregiver = request.IsPrimaryCaregiver,
                CanViewHealthData = true,
                ReceiveAlerts = true
            };

            await _unitOfWork.UserCardiMembers.AddAsync(userCardiMember);
            await _unitOfWork.SaveChangesAsync();

            // Written inside the transaction, which is the whole design: it commits with the
            // member and the link or not at all. That is what lets a retry read the key and know
            // — rather than guess — whether the attempt it is retrying actually landed.
            //
            // Two requests carrying one key can both pass the pre-read above and both reach here;
            // the unique index then fails the loser's save and the caregiver sees an error. That
            // is left as it is on purpose. Recovering the winner's member would mean recognising a
            // unique-violation, which is a DbUpdateException — an EF Core type this layer cannot
            // name, because Domain and Application carry zero packages (CLAUDE.md). The cost is
            // one error message on a path the client does not take: Continue is disabled while the
            // call is in flight, so the realistic race is a sequential retry, and that retry finds
            // the winner's key in the pre-read and is answered correctly.
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                await _unitOfWork.CardiMemberCreationKeys.AddAsync(new CardiMemberCreationKey
                {
                    UserId = userId,
                    Key = idempotencyKey,
                    CardiMemberId = cardiMember.Id,
                });
                await _unitOfWork.SaveChangesAsync();
            }

            commitAttempted = true;
            await _unitOfWork.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            try
            {
                await _unitOfWork.RollbackTransactionAsync();
            }
            catch
            {
                // Rollback is itself a database call. The original failure is the one the
                // caller must see, and the photo clean-up below must still run.
            }
            finally
            {
                // The member and link are still tracked as Added. Left there, the next save on
                // this scoped unit of work would submit the failed graph again.
                _unitOfWork.ClearTracking();
            }

            if (commitAttempted)
            {
                // The member may exist. Hand the caller the id so the audit entry for this
                // request can still name it and a reconciler can find the row.
                throw new Exceptions.CardiMemberCreationOutcomeUnknownException(cardiMember.Id, ex);
            }

            await DiscardUploadedPhotoAsync(cardiMember);
            throw;
        }

        // After the commit, never before it, and never inside the transaction: trimming this
        // caregiver's spent keys is housekeeping, and housekeeping must not be able to fail a
        // creation that has already succeeded. Best-effort for the same reason.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            try
            {
                await _unitOfWork.CardiMemberCreationKeys.PurgeOlderThanAsync(
                    userId, DateTime.UtcNow - CreationKeyLifetime);
            }
            catch
            {
                // A key that outlives its window costs three columns. Saying so to the caregiver,
                // after their member was created, would cost them the member.
            }
        }

        return new CardiMemberResponse
        {
            Id = cardiMember.Id,
            Name = cardiMember.Name,
            DateOfBirth = cardiMember.DateOfBirth,
            Age = CalculateAge(cardiMember.DateOfBirth),
            Gender = cardiMember.Gender,
            Email = cardiMember.Email,
            Phone = cardiMember.Phone,
            Relationship = Stated(request.RelationshipType),
            IsPrimaryCaregiver = request.IsPrimaryCaregiver,
            IsActive = cardiMember.IsActive,
            CreatedDate = cardiMember.CreatedDate,
            PhotoUrl = await PhotoUrlOf(cardiMember)
        };
    }

    /// <summary>
    /// The member a spent creation key names, if it is still a member this caregiver may be handed
    /// back — active itself, and still theirs to view. Null otherwise, which sends the retry on to
    /// create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are load-bearing, and both are about soft deletion. <c>RemoveAsync</c> leaves
    /// the member row in place with <c>IsActive</c> false and deactivates every link to it, so a
    /// row still being readable says nothing about whether the caregiver can reach it. The link
    /// test also covers a caregiver removed from a member other people still watch: active member,
    /// not theirs.
    /// </para>
    /// <para>
    /// The predicate is <c>IsActive &amp;&amp; CanViewHealthData</c>, matching
    /// <c>CardiMemberAccessService</c>'s view rule rather than a looser one of this method's own.
    /// A caregiver whose health-data permission was revoked must not be handed the member back
    /// through this path either, and two different answers to "may they see this member" is how
    /// that kind of gap survives.
    /// </para>
    /// <para>
    /// The member is read <em>once</em> and the response returned is built from that read, so the
    /// row that was checked is the row that is handed back. What this cannot do is make a
    /// concurrent removal impossible: one landing a millisecond after the response is written
    /// would be missed by any ordering, transaction or revalidation — the retry would return a
    /// member that has this instant stopped existing, and the next read of the dashboard corrects
    /// it. Narrowing that window is worth doing; claiming it is closed would not be true.
    /// </para>
    /// </remarks>
    private async Task<CardiMemberResponse?> ReusableMemberAsync(Guid userId, Guid cardiMemberId)
    {
        var existing = await GetByIdAsync(cardiMemberId);
        if (existing is null || !existing.IsActive)
            return null;

        var links = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId);
        return links.Any(l => l.UserId == userId && l.IsActive && l.CanViewHealthData)
            ? existing
            : null;
    }

    public async Task<CardiMemberResponse?> GetByIdAsync(Guid id)
    {
        var cardiMember = await _unitOfWork.CardiMembers.GetByIdAsync(id);
        if (cardiMember == null) return null;

        // Get relationship info - assuming first relationship for now
        var relationships = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(id);
        var primaryRelationship = relationships.FirstOrDefault();

        return new CardiMemberResponse
        {
            Id = cardiMember.Id,
            Name = cardiMember.Name,
            DateOfBirth = cardiMember.DateOfBirth,
            Age = CalculateAge(cardiMember.DateOfBirth),
            Gender = cardiMember.Gender,
            Email = cardiMember.Email,
            Phone = cardiMember.Phone,
            Relationship = primaryRelationship?.RelationshipType ?? Domain.Enums.RelationshipType.Other,
            IsPrimaryCaregiver = primaryRelationship?.IsPrimaryCaregiver ?? false,
            IsActive = cardiMember.IsActive,
            CreatedDate = cardiMember.CreatedDate,
            PhotoUrl = await PhotoUrlOf(cardiMember)
        };
    }

    public async Task<List<CardiMemberResponse>> GetByOrganizationIdAsync(Guid organizationId)
    {
        var cardiMembers = await _unitOfWork.CardiMembers.GetByOrganizationIdAsync(organizationId);
        var responses = new List<CardiMemberResponse>();

        foreach (var cm in cardiMembers)
        {
            var relationships = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cm.Id);
            var primaryRelationship = relationships.FirstOrDefault();

            responses.Add(new CardiMemberResponse
            {
                Id = cm.Id,
                Name = cm.Name,
                DateOfBirth = cm.DateOfBirth,
                Age = CalculateAge(cm.DateOfBirth),
                Gender = cm.Gender,
                Email = cm.Email,
                Phone = cm.Phone,
                Relationship = primaryRelationship?.RelationshipType ?? Domain.Enums.RelationshipType.Other,
                IsPrimaryCaregiver = primaryRelationship?.IsPrimaryCaregiver ?? false,
                IsActive = cm.IsActive,
                CreatedDate = cm.CreatedDate,
                // Per-member rather than batched: the storage adapter caches signed URLs per
                // object name, so a list re-signs only what no screen has asked for recently.
                PhotoUrl = await PhotoUrlOf(cm)
            });
        }

        return responses;
    }

    public async Task<CardiMemberDetailResponse> GetDetailAsync(
        Guid requestingUserId, Guid cardiMemberId, DateOnly? seriesEndsOn = null, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);
        return await BuildDetailAsync(requestingUserId, member, seriesEndsOn, ct);
    }

    public async Task<CardiMemberDetailResponse> UpdateAsync(
        Guid requestingUserId, Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        // Photo first, before any field is touched: a refused photo fails the whole edit with
        // nothing half-applied. The new object is uploaded under a fresh name and the old one is
        // deleted only AFTER the save succeeds — a failed save must not orphan the member's
        // stored name against a blob that no longer exists. When both PhotoBase64 and RemovePhoto
        // arrive (a client bug the validator rejects), the supplied photo wins.
        string? replacedPhotoObjectName = null;
        if (!string.IsNullOrWhiteSpace(request.PhotoBase64))
        {
            var jpeg = ProcessPhotoOrThrow(request.PhotoBase64);
            var uploaded = await _photoStorage.UploadAsync(member.Id, jpeg, ct);
            replacedPhotoObjectName = member.PhotoObjectName;
            member.PhotoObjectName = uploaded;
        }
        else if (request.RemovePhoto)
        {
            replacedPhotoObjectName = member.PhotoObjectName;
            member.PhotoObjectName = null;
        }
        // Neither supplied: the photo is left alone — same omitted-means-keep stance as Gender.

        member.Name = request.Name;
        member.DateOfBirth = request.DateOfBirth;
        member.Email = request.Email;
        member.Phone = request.Phone;
        member.EmergencyContactName = request.EmergencyContactName;
        member.EmergencyContactPhone = request.EmergencyContactPhone;
        member.MedicalNotes = Protect(request.MedicalNotes);
        member.AlertSensitivity = request.AlertSensitivity;

        // Only when supplied — see UpdateCardiMemberRequest.Gender. A client that does not show
        // the picker must be able to save the rest of the form without clearing a stated sex.
        if (request.Gender.HasValue)
            member.Gender = request.Gender.Value;

        member.UpdatedDate = DateTime.UtcNow;
        _unitOfWork.CardiMembers.Update(member);

        // Relationship lives on the caregiver's own link, not the member — editing it here
        // must not rewrite what other caregivers call this person.
        var link = await FindLinkAsync(requestingUserId, cardiMemberId);
        var relationship = Stated(request.RelationshipType);
        if (link is not null && link.RelationshipType != relationship)
        {
            link.RelationshipType = relationship;
            link.UpdatedDate = DateTime.UtcNow;
            _unitOfWork.UserCardiMembers.Update(link);
        }

        await _unitOfWork.SaveChangesAsync();

        // Only now, with the new state durable, is the superseded blob deleted.
        if (replacedPhotoObjectName is not null)
            await TryDeletePhotoAsync(replacedPhotoObjectName, ct);

        // Saving may have closed a gap we are currently nagging about. Resolving here rather than
        // waiting for the nightly run is what stops a caregiver seeing the card they just actioned
        // still sitting there when the screen pops.
        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return await BuildDetailAsync(requestingUserId, member, seriesEndsOn: null, ct);
    }

    public async Task RemoveAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = DateTime.UtcNow;
        member.IsActive = false;
        member.UpdatedDate = now;

        // The membership is soft-deleted but the photo is not: a full-face image is Tier 1 data
        // and must not outlive the membership. Cleared here, blob deleted after the save lands.
        var photoObjectName = member.PhotoObjectName;
        member.PhotoObjectName = null;

        _unitOfWork.CardiMembers.Update(member);

        // Deactivate the links too, otherwise the member keeps passing access checks and
        // keeps being counted by anything that walks a caregiver's links.
        foreach (var link in await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId))
        {
            if (!link.IsActive) continue;
            link.IsActive = false;
            link.UpdatedDate = now;
            _unitOfWork.UserCardiMembers.Update(link);
        }

        // Devices must stop syncing, and their tokens should not outlive the member.
        foreach (var connection in await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId))
        {
            if (!connection.IsActive) continue;
            connection.IsActive = false;
            connection.ConnectionStatus = ConnectionStatus.Disconnected;
            connection.AccessToken = null;
            connection.RefreshToken = null;
            connection.TokenExpiry = null;
            connection.UpdatedDate = now;
            _unitOfWork.DeviceConnections.Update(connection);
        }

        await _unitOfWork.SaveChangesAsync();

        if (photoObjectName is not null)
            await TryDeletePhotoAsync(photoObjectName, ct);

        // Withdrawn rather than resolved: the gaps were not closed, they stopped applying. Counting
        // a removal as a success would flatter the comply rate the rule review depends on.
        await _gapResolver.WithdrawForCardiMemberAsync(
            cardiMemberId, NotificationResolutionReason.ScopeRemoved, ct);
    }

    public async Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = DateTime.UtcNow;
        member.MonitoringPausedUntil = now.AddHours(request.DurationHours);
        member.MonitoringPauseReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        member.UpdatedDate = now;
        _unitOfWork.CardiMembers.Update(member);
        await _unitOfWork.SaveChangesAsync();

        // Nagging about a member someone deliberately paused is the fastest way to teach a
        // caregiver to ignore the whole surface.
        await _gapResolver.WithdrawForCardiMemberAsync(
            cardiMemberId, NotificationResolutionReason.MonitoringPaused, ct);

        return PauseStateOf(member, now);
    }

    public async Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = DateTime.UtcNow;
        member.MonitoringPausedUntil = null;
        member.MonitoringPauseReason = null;
        member.UpdatedDate = now;
        _unitOfWork.CardiMembers.Update(member);
        await _unitOfWork.SaveChangesAsync();

        // Anything still outstanding comes straight back rather than waiting for tomorrow's run.
        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return PauseStateOf(member, now);
    }

    private async Task<CardiMember> RequireActiveMemberAsync(Guid cardiMemberId)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");
        return member;
    }

    private async Task<UserCardiMember?> FindLinkAsync(Guid userId, Guid cardiMemberId)
    {
        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(userId);
        return links.FirstOrDefault(l => l.CardiMemberId == cardiMemberId && l.IsActive);
    }

    private async Task<CardiMemberDetailResponse> BuildDetailAsync(
        Guid requestingUserId, CardiMember member, DateOnly? seriesEndsOn, CancellationToken ct = default)
    {
        // The relationship shown is the requesting caregiver's own ("Dad"), not whatever the
        // first link in the table happens to say.
        var link = await FindLinkAsync(requestingUserId, member.Id);
        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(member.Id)).ToList();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logs = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                member.Id, today.AddDays(-(BaselineProgress.PeriodDays - 1)), today))
            .ToList();
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(
            member.Id, BaselineProgress.PeriodDays);
        var unresolvedAlerts = await _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(member.Id);

        var now = DateTime.UtcNow;
        var pause = PauseStateOf(member, now);
        var age = CalculateAge(member.DateOfBirth);
        var metrics = logs.Count == 0 ? null : MemberInsightsCalculator.BuildMetrics(logs, baseline, today, age);

        // Judged on today's window, before the series can move: the status is a statement about
        // the member now, and a member with nothing current must not read as steady because an
        // old month's series was asked for.
        var healthStatus = MemberInsightsCalculator.ComputeHealthStatus(unresolvedAlerts, baseline is null, metrics);

        // A journal entry is an account of a closed period and draws the series that ended with
        // it — a Monthbook's ends a month or more ago, which a series that always ran to today
        // could never reach. Only the series moves: the latest reading, its status and its
        // comparison are built from today's window above and stay what they are, because the
        // profile is about now whichever period its charts are drawn for. A member with nothing
        // in today's window still gets the series, on metrics that carry no current reading.
        //
        // The comparison is against the UTC calendar day, which is what the series above ends on.
        // A journal entry is dated on a finished local day, which is never after the UTC day
        // (the furthest-ahead zone's yesterday is UTC's today at most), so a journal date is
        // either earlier — and gets its own window — or equal, where today's window already
        // ends on it. A day too early for its own thirty-day window to exist is nonsense rather
        // than a request, and gets today's series.
        if (seriesEndsOn is { } requested
            && requested < today
            && requested.DayNumber >= BaselineProgress.PeriodDays - 1)
        {
            var seriesLogs = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                    member.Id, requested.AddDays(-(BaselineProgress.PeriodDays - 1)), requested))
                .ToList();
            if (metrics is not null || seriesLogs.Count > 0)
            {
                metrics ??= MemberInsightsCalculator.BuildMetrics(logs, baseline, today, age);
                MemberInsightsCalculator.ReplaceSeries(
                    metrics, MemberInsightsCalculator.BuildMetrics(seriesLogs, baseline, requested, age));
            }
        }
        var lastSyncedAt = member.LastSyncDate ?? connections.Max(c => c.LastSyncDate);
        var latestAssessment = await _unitOfWork.RealtimeAssessments.GetLatestAsync(member.Id, ct);
        var (freshnessTier, freshnessMessage) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt, latestAssessment?.GeneratedAtUtc, now, FirstNameOf(member.Name));

        // Consent checked before the query, not after — same stance as EnvironmentalContextSource:
        // only a consented member can ever have a row, so the common case skips the roundtrip.
        var environmentalReading = member.EnvironmentalContextConsentGranted
            ? await _unitOfWork.EnvironmentalReadings.GetLatestAsync(member.Id, ct)
            : null;

        return new CardiMemberDetailResponse
        {
            Id = member.Id,
            Name = member.Name,
            DateOfBirth = member.DateOfBirth,
            Age = age,
            Gender = member.Gender,
            Email = member.Email,
            Phone = member.Phone,
            Relationship = link?.RelationshipType ?? RelationshipType.Other,
            IsPrimaryCaregiver = link?.IsPrimaryCaregiver ?? false,
            EmergencyContactName = member.EmergencyContactName,
            EmergencyContactPhone = member.EmergencyContactPhone,
            MedicalNotes = Reveal(member.MedicalNotes),
            PhotoUrl = await PhotoUrlOf(member, ct),
            AlertSensitivity = member.AlertSensitivity,
            MonitoringPaused = pause.MonitoringPaused,
            MonitoringPausedUntil = pause.MonitoringPausedUntil,
            MonitoringPauseReason = pause.MonitoringPauseReason,
            MonitoringSince = member.CreatedDate,
            LastSyncedAt = lastSyncedAt,
            ConnectedDeviceCount = connections.Count,
            DataFreshness = freshnessTier,
            DataFreshnessMessage = freshnessMessage,
            Baseline = BaselineProgress.From(logs, baseline),
            HealthStatus = healthStatus,
            Metrics = metrics,
            Weather = WeatherSnapshotMapper.From(
                member.EnvironmentalContextConsentGranted, environmentalReading),
        };
    }

    /// <summary>An elapsed pause is reported as not paused, and never reports a stale reason.</summary>
    private static MonitoringPauseResponse PauseStateOf(CardiMember member, DateTime utcNow)
    {
        var paused = member.IsMonitoringPaused(utcNow);
        return new MonitoringPauseResponse
        {
            MonitoringPaused = paused,
            MonitoringPausedUntil = paused ? member.MonitoringPausedUntil : null,
            MonitoringPauseReason = paused ? member.MonitoringPauseReason : null,
        };
    }

    /// <summary>
    /// Decode + normalise an uploaded photo, before anything is persisted. Bad base64 is the
    /// same class of client fault as a bad image, so both surface as the 400-mapped
    /// <see cref="InvalidProfilePhotoException"/> rather than two differently-shaped failures.
    /// </summary>
    private byte[] ProcessPhotoOrThrow(string photoBase64)
    {
        if (!ProfilePhotoBase64.TryDecode(photoBase64, out var uploadBytes))
            throw new InvalidProfilePhotoException("The photo isn't valid base64 image data.");

        return _photoProcessor.Process(uploadBytes);
    }

    private async Task<string?> PhotoUrlOf(CardiMember member, CancellationToken ct = default) =>
        member.PhotoObjectName is null
            ? null
            : await _photoStorage.GetReadUrlAsync(member.PhotoObjectName, ct);

    /// <summary>
    /// Best-effort blob cleanup, only ever called AFTER the database save that superseded the
    /// object has succeeded. A delete failure is swallowed — the adapter logs it — because the
    /// caregiver's save already happened; failing their request over an orphaned blob would
    /// report their edit as lost when it wasn't.
    /// </summary>
    private async Task TryDeletePhotoAsync(string objectName, CancellationToken ct)
    {
        try
        {
            await _photoStorage.DeleteAsync(objectName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed by design; the storage adapter has already logged the object name.
        }
    }

    private string? Protect(string? medicalNotes) =>
        string.IsNullOrWhiteSpace(medicalNotes) ? null : _encryption.Encrypt(medicalNotes);

    /// <summary>
    /// Medical notes written before they were encrypted are still sitting in the database as
    /// plain text, and AES-GCM's authentication tag makes those indistinguishable from
    /// corruption. Rather than fail the whole screen, fall back to returning the stored value:
    /// a legacy row reads back as what was typed, and every write re-stores it encrypted.
    /// </summary>
    private string? Reveal(string? storedNotes)
    {
        if (string.IsNullOrEmpty(storedNotes))
            return null;

        try
        {
            return _encryption.Decrypt(storedNotes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return storedNotes;
        }
    }

    /// <summary>
    /// Relationship is optional, so an unset or undefined value is stored as
    /// <see cref="RelationshipType.Other"/> — the same "not stated" value the read paths already
    /// fall back to when a caregiver has no link at all. Normalised here rather than at the edge
    /// so nothing can persist a <c>0</c> that later reads back as no valid relationship.
    /// </summary>
    private static RelationshipType Stated(RelationshipType relationship) =>
        Enum.IsDefined(relationship) ? relationship : RelationshipType.Other;

    private int CalculateAge(DateOnly dateOfBirth)
    {
        var today = DateTime.UtcNow;
        var age = today.Year - dateOfBirth.Year;
        if (today.Month < dateOfBirth.Month || (today.Month == dateOfBirth.Month && today.Day < dateOfBirth.Day))
        {
            age--;
        }
        return age;
    }

    /// <summary>
    /// Same first-token split <c>DashboardService</c> uses for the freshness caption, so a
    /// member named "Margaret Doe" is "Margaret" on both screens rather than the full name on
    /// one and the first on the other.
    /// </summary>
    private static string FirstNameOf(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : name;
}
