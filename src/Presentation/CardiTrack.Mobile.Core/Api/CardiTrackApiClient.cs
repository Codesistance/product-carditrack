using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Typed client over the CardiTrack API's ApiResponse envelope. All request bodies are
/// buffered JSON so the auth handler's 401 retry can re-send them.
/// </summary>
public sealed class CardiTrackApiClient : ICardiTrackApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOfflineReadCache? _cache;
    private readonly ITokenStore? _tokens;
    private readonly SessionGeneration? _session;
    private readonly CacheWriteOrder _writeOrder;
    private readonly ILogger<CardiTrackApiClient> _logger;

    /// <summary>
    /// Where each GET's payload came from, keyed by the task that GET returned. Weak on the key,
    /// so an entry lives exactly as long as the caller still holds the call it describes —
    /// nothing to expire, nothing to bound, and no fact about one screen's call left lying around
    /// for another screen to read as its own.
    /// </summary>
    private readonly ConditionalWeakTable<Task, CacheOrigin> _origins = new();

    public CardiTrackApiClient(
        HttpClient http,
        IOfflineReadCache? cache = null,
        ILogger<CardiTrackApiClient>? logger = null,
        ITokenStore? tokens = null,
        SessionGeneration? session = null,
        CacheWriteOrder? writeOrder = null)
    {
        _http = http;
        _cache = cache;
        _tokens = tokens;
        _session = session;
        // Shared by every client in the app when the container supplies it — see CacheWriteOrder
        // for why an order kept per client would be no order at all. The fallback is for a client
        // built by hand: one instance's reads are still ordered among themselves.
        _writeOrder = writeOrder ?? new CacheWriteOrder();
        _logger = logger ?? NullLogger<CardiTrackApiClient>.Instance;
    }

    public Task<OnboardingStatusResponse> GetOnboardingStatusAsync(CancellationToken ct = default) =>
        GetAsync<OnboardingStatusResponse>("api/Onboarding/status", ct);

    public Task<OnboardingSetupResponse> SetupAsync(OnboardingSetupRequest request, CancellationToken ct = default) =>
        PostAsync<OnboardingSetupRequest, OnboardingSetupResponse>("api/Onboarding/setup", request, ct);



    public async Task<CardiMemberResponse> CreateCardiMemberAsync(
        CreateCardiMemberRequest request, CancellationToken ct = default, string? idempotencyKey = null)
    {
        const string path = "api/Onboarding/cardimember";
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await PostAsync<CreateCardiMemberRequest, CardiMemberResponse>(path, request, ct);

        // Built by hand rather than through PostAsJsonAsync, only because this is the one call
        // with a header to set. JsonContent, like the shared helpers use, because it re-serializes
        // on each read — which is what lets the auth handler re-send this request after a 401.
        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        message.Content = JsonContent.Create(request, mediaType: null, Json);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError("POST", path, ex, ct);
        }

        return await ReadEnvelopeAsync<CardiMemberResponse>("POST", path, response, ct);
    }

    public Task<List<CardiMemberResponse>> GetCardiMembersAsync(CancellationToken ct = default) =>
        GetAsync<List<CardiMemberResponse>>(ApiPaths.CardiMembers, ct);

    public Task<List<CardiMemberResponse>?> PeekCardiMembersAsync(CancellationToken ct = default) =>
        PeekAsync<List<CardiMemberResponse>>(ApiPaths.CardiMembers, ct);

    public Task<CardiMemberDetailResponse> GetCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CardiMemberDetailResponse>(ApiPaths.CardiMember(cardiMemberId), ct);

    public Task<CardiMemberDetailResponse?> PeekCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<CardiMemberDetailResponse>(ApiPaths.CardiMember(cardiMemberId), ct);

    // Not cached: MemberProfileKeys cannot spell every date this could be asked for, so a
    // cached copy would outlive an edit, a pause or a removal of the member it describes.
    public Task<CardiMemberDetailResponse> GetCardiMemberAsync(
        Guid cardiMemberId, DateOnly seriesEndsOn, CancellationToken ct = default) =>
        GetAsync<CardiMemberDetailResponse>(ApiPaths.CardiMember(cardiMemberId, seriesEndsOn), ct, cache: false);

    public async Task<CardiMemberDetailResponse> UpdateCardiMemberAsync(
        Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default)
    {
        var updated = await SendAsync<UpdateCardiMemberRequest, CardiMemberDetailResponse>(
            HttpMethod.Put, ApiPaths.CardiMember(cardiMemberId), request, ct);
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return updated;
    }

    public async Task<CardiMemberDetailResponse> ConfirmMedicalNotesAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var confirmed = await SendAsync<CardiMemberDetailResponse>(
            HttpMethod.Post, $"{ApiPaths.CardiMember(cardiMemberId)}/medical-notes/confirm", ct);
        // The profile's own keys, the same ones an edit evicts: the review date this just moved is
        // part of that payload, and a cached copy would go on showing the old one.
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return confirmed;
    }

    public async Task RemoveCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, ApiPaths.CardiMember(cardiMemberId), ct);
        // Every key for this member the client can spell. Parameterised list pages (a filtered
        // alert list, a journal search) cannot be enumerated without an index and age out
        // within IOfflineReadCache.Lifetime or at sign-out; the plain ones go now.
        await EvictAsync(
            ApiPaths.CardiMember(cardiMemberId),
            ApiPaths.Dashboard(cardiMemberId),
            ApiPaths.Devices(cardiMemberId),
            ApiPaths.AlertPreferences(cardiMemberId),
            ApiPaths.MemberAlarms(cardiMemberId),
            ApiPaths.JournalSettings(cardiMemberId),
            ApiPaths.CurrentStatus(cardiMemberId),
            ApiPaths.Digest(cardiMemberId),
            ApiPaths.Advise(cardiMemberId),
            ApiPaths.Trend(cardiMemberId),
            ApiPaths.Questionnaires(cardiMemberId, null, DefaultQuestionnairePage, DefaultQuestionnairePageSize),
            ApiPaths.Alerts(null, null, null, null, null, cardiMemberId),
            ApiPaths.CardiMembers,
            ApiPaths.NotificationSummary);
    }

    public async Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default)
    {
        var paused = await PostAsync<PauseMonitoringRequest, MonitoringPauseResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/pause", request, ct);
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return paused;
    }

    public async Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var resumed = await SendAsync<MonitoringPauseResponse>(
            HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/pause", ct);
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return resumed;
    }

    public Task<AlertPreferencesResponse> GetAlertPreferencesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<AlertPreferencesResponse>(ApiPaths.AlertPreferences(cardiMemberId), ct);

    public Task<AlertPreferencesResponse?> PeekAlertPreferencesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<AlertPreferencesResponse>(ApiPaths.AlertPreferences(cardiMemberId), ct);

    public async Task<AlertRuleSettingResponse> SetAlertRuleEnabledAsync(
        Guid cardiMemberId, string ruleId, bool enabled, CancellationToken ct = default)
    {
        var setting = await SendAsync<SetAlertRuleEnabledRequest, AlertRuleSettingResponse>(
            HttpMethod.Patch,
            $"api/v1/cardimembers/{cardiMemberId}/alert-preferences/rules/{Uri.EscapeDataString(ruleId)}",
            new SetAlertRuleEnabledRequest { Enabled = enabled },
            ct);
        await EvictAsync(ApiPaths.AlertPreferences(cardiMemberId));
        return setting;
    }

    public Task<AlarmCatalogueResponse> GetAlarmCatalogueAsync(CancellationToken ct = default) =>
        GetAsync<AlarmCatalogueResponse>(ApiPaths.AlarmCatalogue, ct);

    public Task<AlarmCatalogueResponse?> PeekAlarmCatalogueAsync(CancellationToken ct = default) =>
        PeekAsync<AlarmCatalogueResponse>(ApiPaths.AlarmCatalogue, ct);

    public Task<IReadOnlyList<MetricAlarmResponse>> GetMemberAlarmsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MetricAlarmResponse>>(ApiPaths.MemberAlarms(cardiMemberId), ct);

    public Task<IReadOnlyList<MetricAlarmResponse>?> PeekMemberAlarmsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<MetricAlarmResponse>>(ApiPaths.MemberAlarms(cardiMemberId), ct);

    public async Task<MetricAlarmResponse> CreateMemberAlarmAsync(
        Guid cardiMemberId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        var created = await SendAsync<SaveMetricAlarmRequest, MetricAlarmResponse>(
            HttpMethod.Post, ApiPaths.MemberAlarms(cardiMemberId), request, ct);
        await EvictAsync(ApiPaths.MemberAlarms(cardiMemberId));
        return created;
    }

    public async Task<MetricAlarmResponse> SaveMemberAlarmAsync(
        Guid cardiMemberId, Guid alarmId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        var saved = await SendAsync<SaveMetricAlarmRequest, MetricAlarmResponse>(
            HttpMethod.Put, $"api/v1/cardimembers/{cardiMemberId}/alarms/{alarmId}", request, ct);
        await EvictAsync(ApiPaths.MemberAlarms(cardiMemberId));
        return saved;
    }

    public async Task DeleteMemberAlarmAsync(
        Guid cardiMemberId, Guid alarmId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/alarms/{alarmId}", ct);
        await EvictAsync(ApiPaths.MemberAlarms(cardiMemberId));
    }

    public Task<JournalSettingsResponse> GetJournalSettingsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<JournalSettingsResponse>(ApiPaths.JournalSettings(cardiMemberId), ct);

    public Task<JournalSettingsResponse?> PeekJournalSettingsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<JournalSettingsResponse>(ApiPaths.JournalSettings(cardiMemberId), ct);

    public async Task<JournalSettingsResponse> UpdateJournalSettingsAsync(
        Guid cardiMemberId, UpdateJournalSettingsRequest request, CancellationToken ct = default)
    {
        var settings = await SendAsync<UpdateJournalSettingsRequest, JournalSettingsResponse>(
            HttpMethod.Put, ApiPaths.JournalSettings(cardiMemberId), request, ct);
        await EvictAsync(ApiPaths.JournalSettings(cardiMemberId));
        return settings;
    }

    public Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DashboardResponse>(ApiPaths.Dashboard(cardiMemberId), ct);

    public Task<DashboardResponse?> PeekDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<DashboardResponse>(ApiPaths.Dashboard(cardiMemberId), ct);

    public Task<CurrentStatusMessageResponse> GetCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CurrentStatusMessageResponse>(ApiPaths.CurrentStatus(cardiMemberId), ct);

    public Task<CurrentStatusMessageResponse?> PeekCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<CurrentStatusMessageResponse>(ApiPaths.CurrentStatus(cardiMemberId), ct);

    public Task<DigestResponse> GetDigestAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DigestResponse>(ApiPaths.Digest(cardiMemberId), ct);

    public Task<DigestResponse?> PeekDigestAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<DigestResponse>(ApiPaths.Digest(cardiMemberId), ct);

    public Task<AdviseResponse> GetAdviseAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<AdviseResponse>(ApiPaths.Advise(cardiMemberId), ct);

    public Task<AdviseResponse?> PeekAdviseAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<AdviseResponse>(ApiPaths.Advise(cardiMemberId), ct);

    /// <summary>
    /// The stored trend narrative. A 403 evicts the cached copy on the way out: the caregiver's
    /// access to this member has been withdrawn, and the narrative is health content about
    /// someone they may no longer see. Leaving it in the cache would let the next open without a
    /// connection render it from disk, which is the withdrawal undone by the offline path.
    /// </summary>
    public async Task<TrendInsightResponse> GetTrendAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<TrendInsightResponse>(ApiPaths.Trend(cardiMemberId), ct);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await EvictAsync(ApiPaths.Trend(cardiMemberId));
            throw;
        }
    }

    public Task<TrendInsightResponse?> PeekTrendAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<TrendInsightResponse>(ApiPaths.Trend(cardiMemberId), ct);

    /// <summary>
    /// The first page of a member's questions as every screen asks for it — the detail screen's
    /// call takes the defaults, and the questionnaires screen's own constant matches them.
    /// </summary>
    private const int DefaultQuestionnairePage = OfflineReadDefaults.QuestionnairePage;
    private const int DefaultQuestionnairePageSize = OfflineReadDefaults.QuestionnairePageSize;

    /// <summary>The keys a change to one member's profile or monitoring state makes stale.</summary>
    /// <summary>
    /// How many times each key has been evicted in this client's lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per key rather than one count for the client, and the difference is not academic: a 404
    /// evicts the path that returned it, and <c>OfflineCacheWarmer</c> fetches a member's screens
    /// together. A single expected miss in that batch — a member with no digest yet — would bar
    /// every other read running beside it from caching, and the warm would silently do nothing.
    /// A test caught exactly that.
    /// </para>
    /// <para>
    /// Concurrent because a GET and a mutation reach this from different threads by design. It
    /// grows with the distinct keys evicted in one client's life, which is bounded by the paths
    /// the app can spell.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, int> _evictions = new(StringComparer.Ordinal);

    private int EvictionsOf(string path) => _evictions.TryGetValue(path, out var count) ? count : 0;

    /// <summary>
    /// Bumped when the whole cache is dropped rather than one key of it.
    /// </summary>
    /// <remarks>
    /// <see cref="_evictions"/> cannot express "every key", and enumerating them would only cover
    /// the ones this client has already touched. A read that began before a clear carries the
    /// epoch from before it and is refused at save time, which is the same guarantee per-key
    /// eviction gives, for the case that has no key.
    /// </remarks>
    private int _cacheEpoch;


    private static string[] MemberProfileKeys(Guid cardiMemberId) =>
    [
        ApiPaths.CardiMember(cardiMemberId),
        ApiPaths.Dashboard(cardiMemberId),
        ApiPaths.CardiMembers,
        // The stored interpretations, because pausing monitoring is exactly when they stop being
        // true. The server withholds them for a paused member, but the Journal tab falls back to
        // its cached copy when a request fails, and a cached pre-pause narrative would then be
        // rendered for monitoring that has stopped — the suppression undone by the cache.
        ApiPaths.Trend(cardiMemberId),
    ];

    /// <summary>
    /// The one call in this client whose answer nobody reads. It returns 202 the moment the API
    /// has noted the arrival, so it needs no special timeout and gets the default: the model load
    /// it may start happens on the server, long after this has returned.
    /// </summary>
    public Task PrepareAssistantAsync(CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Post, "api/v1/assistant/prepare", ct);

    /// <summary>
    /// How long a member-chat send may run before the app hangs up. It has to outlast the server's
    /// own ceiling for the whole request, so that the server — not the app — is the one to give
    /// up, and says so. The API caps a send end to end at MemberChat:SendBudgetSeconds (1020s:
    /// room for a clinical read queued behind another caller on the single-request MedGemma
    /// service, plus the Vertex calls around it), Terraform sets its Cloud Run request timeout a
    /// minute past that (1080s), and this sits a further minute out — the same "the outer layer
    /// must outlast the inner one by a margin" rule at every layer. Giving up first here doesn't
    /// stop the work; it just means nobody is listening for the answer it produces.
    /// </summary>
    public static readonly TimeSpan MemberChatSendTimeout = TimeSpan.FromSeconds(1140);

    /// <summary>
    /// The value for <see cref="HttpClient.Timeout"/>. That timeout is a hard, client-wide ceiling
    /// that a per-request <see cref="TimeoutHandler"/> budget can only shorten, never extend, so it
    /// has to sit above the slowest request this client makes. Derived from the send timeout
    /// rather than restated: when it was a separate literal (190s) it silently capped the
    /// member-chat send at a fifth of its intended budget.
    /// </summary>
    public static readonly TimeSpan HttpClientCeiling = MemberChatSendTimeout + TimeSpan.FromSeconds(30);

    public async Task<MemberChatMessageResponse> SendMemberChatMessageAsync(
        Guid cardiMemberId, MemberChatMessageRequest request, CancellationToken ct = default)
    {
        var sent = await SendAsync<MemberChatMessageRequest, MemberChatMessageResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/messages", request, ct,
            timeout: MemberChatSendTimeout);
        await EvictAsync(MemberChatKeys(cardiMemberId));
        return sent;
    }

    /// <summary>
    /// The send as a stream: each <c>step</c> event is handed to <paramref name="onStep"/> as it
    /// arrives, and the <c>answer</c> event is the result. Failures read the same as
    /// <see cref="SendMemberChatMessageAsync"/>'s — an error status before the stream starts, or an
    /// <c>error</c> event after, both become an <see cref="ApiException"/> carrying the server's
    /// status and message.
    /// </summary>
    /// <remarks>
    /// The whole exchange runs under <see cref="MemberChatSendTimeout"/>, not just the wait for
    /// headers: the handler's per-request timeout ends when the headers arrive, and so does
    /// <see cref="HttpClient.Timeout"/> for a response read as it streams.
    /// </remarks>
    public async Task<MemberChatMessageResponse> StreamMemberChatMessageAsync(
        Guid cardiMemberId, MemberChatMessageRequest request, IProgress<MemberChatStep>? onStep,
        CancellationToken ct = default)
    {
        var path = $"api/v1/member-chat/members/{cardiMemberId}/messages/stream";
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(ct);
        whole.CancelAfter(MemberChatSendTimeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            // JsonContent re-serializes on each read, so the auth handler's 401 retry can re-send.
            Content = JsonContent.Create(request, mediaType: null, Json),
        };
        message.Options.Set(TimeoutHandler.TimeoutOption, MemberChatSendTimeout);
        message.Headers.Accept.ParseAdd("text/event-stream");

        MemberChatMessageResponse? answer = null;
        var streamStarted = false;
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, whole.Token);
            streamStarted = response.IsSuccessStatusCode;
            if (!response.IsSuccessStatusCode)
                // Under the whole-send budget too: past the headers the handler's timeout has
                // stopped, and an error body that stalls would otherwise hold the send open.
                throw await MapErrorAsync("POST", path, response, whole.Token);

            await using var body = await response.Content.ReadAsStreamAsync(whole.Token);
            await foreach (var sse in ServerSentEventReader.ReadAsync(body, whole.Token))
            {
                switch (sse.Name)
                {
                    case "step":
                        if (onStep is not null && JsonUtility.TryDeserialize<MemberChatStep>(sse.Data, out var step, out _))
                            onStep.Report(step!);
                        break;
                    case "answer":
                        // Unreadable is treated as absent — the "cut off" message below — rather
                        // than letting a JsonException escape as an unexplained failure.
                        if (JsonUtility.TryDeserialize<MemberChatMessageResponse>(sse.Data, out var parsed, out _))
                            answer = parsed;
                        break;
                    case "error":
                        throw StreamError(path, sse.Data);
                    case "done":
                        break;
                }

                if (sse.Name == "done")
                    break;
            }
        }
        catch (Exception ex) when (answer is not null && ex is not ApiException
                                   && (IsTransport(ex) || ex is IOException) && !ct.IsCancellationRequested)
        {
            // The answer had already arrived and the turn is saved server-side: a connection that
            // drops (or a budget that runs out) before `done` costs the caregiver nothing.
            _logger.LogWarning(ex, "API POST {Path} stream broke after its answer; keeping the answer", path);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && whole.IsCancellationRequested)
        {
            // Our own budget ran out mid-stream — the far side was too slow, not the caller
            // giving up, and NetworkError words the two differently.
            throw NetworkError("POST", path, new TimeoutException("The streamed reply did not finish in time.", ex), ct);
        }
        catch (Exception ex) when (ex is not ApiException && (IsTransport(ex) || ex is IOException))
        {
            throw NetworkError("POST", path, ex, ct);
        }
        finally
        {
            // Once the server accepted the send it may have saved the turn, whatever happened to
            // the stream after — so the cached thread goes stale on every outcome from there,
            // not only on an answer, or the next load would hide a reply that exists.
            if (streamStarted)
                await EvictAsync(MemberChatKeys(cardiMemberId));
        }

        // An answer that arrived before the connection dropped is still the answer: done is only
        // the terminator, and the reply is already saved server-side.
        if (answer is null)
        {
            _logger.LogError("API POST {Path} stream ended without an answer", path);
            throw new ApiException(HttpStatusCode.ServiceUnavailable,
                "The reply was cut off on its way here. Pull down to refresh the conversation.");
        }

        return answer;
    }

    private ApiException StreamError(string path, string data)
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var text = "The assistant couldn't answer that just now — try again in a moment.";
        if (JsonUtility.TryDeserialize<StreamErrorEvent>(data, out var error, out _))
        {
            if (error!.Status is >= 400 and <= 599)
                status = (HttpStatusCode)error.Status;
            if (!string.IsNullOrWhiteSpace(error.Message))
                text = error.Message;
        }

        _logger.Log((int)status >= 500 ? LogLevel.Error : LogLevel.Warning,
            "API POST {Path} stream ended with {StatusCode}: {ServerMessage}", path, (int)status, text);
        return new ApiException(status, text);
    }

    /// <summary>The stream's <c>error</c> event: the status and message the JSON endpoint would
    /// have answered with.</summary>
    private sealed class StreamErrorEvent
    {
        public int Status { get; init; }
        public string? Message { get; init; }
    }

    public async Task<MemberChatHistoryResponse?> GetCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var history = await GetAsync<MemberChatHistoryResponse?>(
            ApiPaths.CurrentMemberChatSession(cardiMemberId), ct,
            // 200 with a null data is this endpoint's documented "no active session yet" —
            // see MemberChatController.GetCurrentSession — not a malformed reply.
            allowNullData: true);
        if (history is null)
            await EvictAsync(ApiPaths.CurrentMemberChatSession(cardiMemberId));
        return history;
    }

    public Task<MemberChatHistoryResponse?> PeekCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<MemberChatHistoryResponse>(ApiPaths.CurrentMemberChatSession(cardiMemberId), ct);

    public Task<MemberChatSessionListResponse> GetMemberChatSessionsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<MemberChatSessionListResponse>(
            ApiPaths.MemberChatSessions(cardiMemberId), ct);

    public Task<MemberChatSessionListResponse?> PeekMemberChatSessionsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<MemberChatSessionListResponse>(ApiPaths.MemberChatSessions(cardiMemberId), ct);

    public Task<MemberChatHistoryResponse> GetMemberChatSessionAsync(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct = default) =>
        GetAsync<MemberChatHistoryResponse>(
            $"api/v1/member-chat/members/{cardiMemberId}/sessions/{sessionId}", ct);

    public async Task<MemberChatEndSessionResponse> EndCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var ended = await SendAsync<MemberChatEndSessionResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/sessions/current/end", ct);
        await EvictAsync(MemberChatKeys(cardiMemberId));
        return ended;
    }

    public async Task<MemberChatHistoryResponse> ContinueMemberChatSessionAsync(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct = default)
    {
        var continued = await SendAsync<MemberChatHistoryResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/sessions/{sessionId}/continue", ct);
        await EvictAsync(MemberChatKeys(cardiMemberId));
        return continued;
    }

    public async Task<MemberChatDeleteSessionsResponse> DeleteMemberChatSessionsAsync(
        Guid cardiMemberId, IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
    {
        var deleted = await SendAsync<MemberChatDeleteSessionsRequest, MemberChatDeleteSessionsResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/sessions/delete",
            new MemberChatDeleteSessionsRequest { SessionIds = [.. sessionIds] }, ct);
        await EvictAsync(MemberChatKeys(cardiMemberId));
        return deleted;
    }

    /// <summary>The keys a chat mutation makes stale — the open thread and the history list.</summary>
    private static string[] MemberChatKeys(Guid cardiMemberId) =>
    [
        ApiPaths.CurrentMemberChatSession(cardiMemberId),
        ApiPaths.MemberChatSessions(cardiMemberId),
    ];

    public Task<MemberChatSuggestionsResponse> GetMemberChatSuggestionsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<MemberChatSuggestionsResponse>(
            ApiPaths.MemberChatSuggestions(cardiMemberId), ct);

    public Task<MemberChatSuggestionsResponse?> PeekMemberChatSuggestionsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<MemberChatSuggestionsResponse>(ApiPaths.MemberChatSuggestions(cardiMemberId), ct);

    public Task<IReadOnlyList<DigestResponse>> GetJournalEntriesAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DigestResponse>>(
            ApiPaths.JournalEntries(cardiMemberId, cadence, limit, search, from, urgency), ct);

    public Task<IReadOnlyList<DigestResponse>?> PeekJournalEntriesAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<DigestResponse>>(
            ApiPaths.JournalEntries(cardiMemberId, cadence, limit, search, from, urgency), ct);

    public Task<DigestResponse> GetJournalEntryAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        DateOnly localDate,
        CancellationToken ct = default) =>
        GetAsync<DigestResponse>(ApiPaths.JournalEntry(cardiMemberId, cadence, localDate), ct);

    public Task<DigestResponse?> PeekJournalEntryAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        DateOnly localDate,
        CancellationToken ct = default) =>
        PeekAsync<DigestResponse>(ApiPaths.JournalEntry(cardiMemberId, cadence, localDate), ct);

    public Task<QuestionnairesPageResponse> GetQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = DefaultQuestionnairePage,
        int pageSize = DefaultQuestionnairePageSize,
        CancellationToken ct = default) =>
        GetAsync<QuestionnairesPageResponse>(ApiPaths.Questionnaires(cardiMemberId, search, page, pageSize), ct);

    public Task<QuestionnairesPageResponse?> PeekQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = DefaultQuestionnairePage,
        int pageSize = DefaultQuestionnairePageSize,
        CancellationToken ct = default) =>
        PeekAsync<QuestionnairesPageResponse>(ApiPaths.Questionnaires(cardiMemberId, search, page, pageSize), ct);

    public async Task<QuestionnaireResponse> AnswerQuestionnaireAsync(
        Guid questionnaireId, AnswerQuestionnaireRequest request, CancellationToken ct = default)
    {
        var answered = await SendAsync<AnswerQuestionnaireRequest, QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/answer", request, ct);
        await EvictAsync(QuestionnaireKeys(answered));
        return answered;
    }

    public async Task<QuestionnaireResponse> DismissQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default)
    {
        var dismissed = await SendAsync<QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/dismiss", ct);
        await EvictAsync(QuestionnaireKeys(dismissed));
        return dismissed;
    }

    public async Task<QuestionnaireResponse> ExpireQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default)
    {
        var expired = await SendAsync<QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/expire", ct);
        await EvictAsync(QuestionnaireKeys(expired));
        return expired;
    }

    /// <summary>
    /// A question answered, skipped or retired changes the member's first page of questions and
    /// the dashboard and detail cards that surface the pending one. The member id comes off the
    /// response, since the call itself only knows the question.
    /// </summary>
    private static string[] QuestionnaireKeys(QuestionnaireResponse questionnaire) =>
    [
        ApiPaths.Questionnaires(questionnaire.CardiMemberId, null, DefaultQuestionnairePage, DefaultQuestionnairePageSize),
        ApiPaths.Dashboard(questionnaire.CardiMemberId),
        ApiPaths.CardiMember(questionnaire.CardiMemberId),
    ];

    public Task DeleteQuestionnaireAsync(Guid questionnaireId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/questionnaires/{questionnaireId}", ct);

    public async Task<QuestionnaireResponse> OfferStandingFactAsync(
        Guid cardiMemberId, OfferStandingFactRequest request, CancellationToken ct = default)
    {
        var offered = await SendAsync<OfferStandingFactRequest, QuestionnaireResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/questionnaires", request, ct);
        await EvictAsync(QuestionnaireKeys(offered));
        return offered;
    }

    public Task<AlertListResponse> GetAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        Guid? cardiMemberId = null,
        CancellationToken ct = default) =>
        GetAsync<AlertListResponse>(ApiPaths.Alerts(severity, status, from, to, limit, cardiMemberId), ct);

    public Task<AlertListResponse?> PeekAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        Guid? cardiMemberId = null,
        CancellationToken ct = default) =>
        // The same key the live call writes under, so a peek can only ever answer the exact
        // question the screen is about to ask the API — never a neighbouring filter's page.
        PeekAsync<AlertListResponse>(ApiPaths.Alerts(severity, status, from, to, limit, cardiMemberId), ct);

    public Task<AlertDetailResponse> GetAlertAsync(Guid alertId, CancellationToken ct = default) =>
        GetAsync<AlertDetailResponse>(ApiPaths.Alert(alertId), ct);

    public Task<AlertDetailResponse?> PeekAlertAsync(Guid alertId, CancellationToken ct = default) =>
        PeekAsync<AlertDetailResponse>(ApiPaths.Alert(alertId), ct);

    // Alert mutations evict the detail only. The list keys are parameterised by filter and the
    // dashboard by a member id these calls do not have; both screens re-fetch live on every
    // landing and tick, and the alerts list already masks the gap with its pending-deletes set.
    public async Task<AlertAcknowledgementResponse> AcknowledgeAlertAsync(
        Guid alertId, AlertAnswerRequest? answer = null, CancellationToken ct = default)
    {
        // A null answer sends no body at all, which is the form this call had before answers
        // existed and the one the server still accepts; an answer rides along as JSON.
        var acknowledged = await SendAsync<AlertAnswerRequest, AlertAcknowledgementResponse>(
            HttpMethod.Post, $"api/v1/alerts/{alertId}/acknowledge", answer, ct);
        await EvictAsync(ApiPaths.Alert(alertId));
        return acknowledged;
    }

    public async Task<AlertAcknowledgementResponse> CloseAlertAsync(
        Guid alertId, AlertAnswerRequest? answer = null, CancellationToken ct = default)
    {
        var closed = await SendAsync<AlertAnswerRequest, AlertAcknowledgementResponse>(
            HttpMethod.Post, ApiPaths.AlertClose(alertId), answer, ct);
        await EvictAsync(ApiPaths.Alert(alertId));
        return closed;
    }

    public async Task<AlertAcknowledgementResponse> UnacknowledgeAlertAsync(
        Guid alertId, CancellationToken ct = default)
    {
        var unacknowledged = await SendAsync<AlertAcknowledgementResponse>(
            HttpMethod.Delete, $"api/v1/alerts/{alertId}/acknowledge", ct);
        await EvictAsync(ApiPaths.Alert(alertId));
        return unacknowledged;
    }

    public async Task DeleteAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, ApiPaths.Alert(alertId), ct);
        await EvictAsync(ApiPaths.Alert(alertId));
    }

    public Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DeviceListResponse>(ApiPaths.Devices(cardiMemberId), ct);

    public Task<DeviceListResponse?> PeekDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<DeviceListResponse>(ApiPaths.Devices(cardiMemberId), ct);

    public async Task DisconnectDeviceAsync(Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
    }

    public async Task<DeviceResponse> SetPrimaryDeviceAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await SendAsync<DeviceResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/primary", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
        return device;
    }

    public async Task<DeviceResponse> RefreshDeviceConnectionAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await SendAsync<DeviceResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/refresh", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
        return device;
    }

    public async Task<DeviceSyncResultResponse> SyncDevicesAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var result = await SendAsync<DeviceSyncResultResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/sync", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
        return result;
    }

    public async Task<DeviceHistoryRepullResponse> RequestHistoryRepullAsync(
        Guid cardiMemberId, Guid deviceId, int days, CancellationToken ct = default)
    {
        var repull = await PostAsync<HistoryRepullRequest, DeviceHistoryRepullResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/history-repull",
            new HistoryRepullRequest { Days = days },
            ct);
        // The queued request is part of the device list's own payload (historyRepull), so a
        // cached list would still show the action on offer moments after it was taken.
        await EvictAsync(DeviceKeys(cardiMemberId));
        return repull;
    }

    /// <summary>What a device coming, going or syncing makes stale: the device list and the two reads that quote it.</summary>
    private static string[] DeviceKeys(Guid cardiMemberId) =>
    [
        ApiPaths.Devices(cardiMemberId),
        ApiPaths.Dashboard(cardiMemberId),
        ApiPaths.CardiMember(cardiMemberId),
    ];

    public Task<OAuthInitiationResponse> InitiateDeviceConnectionAsync(Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default) =>
        PostAsync<ConnectDeviceRequest, OAuthInitiationResponse>($"api/v1/cardimembers/{cardiMemberId}/devices", request, ct);

    public Task<DeviceResponse> CompleteDeviceConnectionAsync(string provider, OAuthCallbackRequest request, CancellationToken ct = default) =>
        PostAsync<OAuthCallbackRequest, DeviceResponse>($"api/v1/oauth/callback/{provider}", request, ct);

    public Task<DeviceInviteResponse> CreateDeviceInviteAsync(
        Guid cardiMemberId, CreateDeviceInviteRequest request, CancellationToken ct = default) =>
        PostAsync<CreateDeviceInviteRequest, DeviceInviteResponse>(
            DeviceInvites(cardiMemberId), request, ct);

    /// <remarks>
    /// <para>
    /// Uncached on purpose. This is polled while the wearer decides, and the whole point of each
    /// call is to find out whether the answer has changed since the last one — a cached read would
    /// report "still pending" long after they had finished.
    /// </para>
    /// <para>
    /// A <c>completed</c> answer is also the only notice this client gets that a device was
    /// connected: the grant happened on the wearer's phone, so nothing here posted anything that
    /// would have evicted the member's snapshots. Without this, the screen that follows reads a
    /// device list assembled before the connection existed, finds no device with the new id, and
    /// falls back to an older connection of the same brand — and the dashboard behind it keeps
    /// saying no device is connected.
    /// </para>
    /// </remarks>
    public async Task<DeviceInviteResponse> GetDeviceInviteAsync(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct = default)
    {
        var invite = await GetAsync<DeviceInviteResponse>(
            $"{DeviceInvites(cardiMemberId)}/{inviteId}", ct, cache: false);

        if (string.Equals(invite.Status, "completed", StringComparison.OrdinalIgnoreCase))
            await EvictAsync(DeviceKeys(cardiMemberId));

        return invite;
    }

    public async Task<DeviceInviteResponse> RevokeDeviceInviteAsync(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct = default)
    {
        var invite = await SendAsync<DeviceInviteResponse>(
            HttpMethod.Delete, $"{DeviceInvites(cardiMemberId)}/{inviteId}", ct);

        // A cancel that lost the race to the wearer finishing comes back "completed", which means a
        // device was just connected — so the member's device list this client holds is stale.
        if (string.Equals(invite.Status, "completed", StringComparison.OrdinalIgnoreCase))
            await EvictAsync(DeviceKeys(cardiMemberId));

        return invite;
    }

    private static string DeviceInvites(Guid cardiMemberId) =>
        $"api/v1/cardimembers/{cardiMemberId}/device-invites";

    // ---- Families ----

    public Task<IReadOnlyList<FamilySummary>> GetMyFamiliesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FamilySummary>>(ApiPaths.MyFamilies, ct);

    public Task<IReadOnlyList<FamilySummary>?> PeekMyFamiliesAsync(CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<FamilySummary>>(ApiPaths.MyFamilies, ct);

    public Task<IReadOnlyList<FamilyMemberSummary>> GetFamilyMembersAsync(
        Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FamilyMemberSummary>>(ApiPaths.FamilyMembers(organizationId), ct);

    public Task<IReadOnlyList<FamilyMemberSummary>?> PeekFamilyMembersAsync(
        Guid organizationId, CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<FamilyMemberSummary>>(ApiPaths.FamilyMembers(organizationId), ct);

    public async Task<IReadOnlyList<FamilyMemberSummary>> TransferFamilyAdminAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        var roster = await SendAsync<TransferFamilyAdminRequest, IReadOnlyList<FamilyMemberSummary>>(
            HttpMethod.Put, ApiPaths.FamilyAdmin(organizationId),
            new TransferFamilyAdminRequest { UserId = userId }, ct);
        // The caller's own role changed, so the family list is stale as well as the roster.
        await EvictAsync(ApiPaths.FamilyMembers(organizationId), ApiPaths.MyFamilies);
        return roster;
    }

    public async Task RemoveFamilyMemberAsync(Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"{ApiPaths.FamilyMembers(organizationId)}/{userId}", ct);
        await EvictAsync(ApiPaths.FamilyMembers(organizationId), ApiPaths.MyFamilies);
    }

    public async Task LeaveFamilyAsync(Guid organizationId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"{ApiPaths.FamilyMembers(organizationId)}/me", ct);

        // Everything, not the three keys this call could name. The caller has just given up the
        // right to read a family's members, and their readings, alerts and journals are saved
        // under paths this method does not know — a member id it never saw, an alert list under
        // whichever filter was last used. Dropping the lot costs a cold re-fetch of the families
        // they are still in; keeping any of it means an offline launch can still draw somebody
        // they no longer watch.
        //
        // The bump is what stops a read that was already in flight from saving its answer back
        // into the cache a moment after it was cleared.
        await ClearCacheAsync();
    }

    public async Task<FamilyJoinRequestReceipt> RequestToJoinFamilyAsync(string familyId, CancellationToken ct = default)
    {
        var receipt = await PostAsync<JoinFamilyRequest, FamilyJoinRequestReceipt>(
            ApiPaths.JoinRequests, new JoinFamilyRequest { FamilyId = familyId }, ct);
        await EvictAsync(ApiPaths.MyJoinRequests);
        return receipt;
    }

    public Task<IReadOnlyList<FamilyJoinRequestSummary>> GetMyJoinRequestsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FamilyJoinRequestSummary>>(ApiPaths.MyJoinRequests, ct);

    public Task<IReadOnlyList<FamilyJoinRequestSummary>?> PeekMyJoinRequestsAsync(CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<FamilyJoinRequestSummary>>(ApiPaths.MyJoinRequests, ct);

    public async Task WithdrawJoinRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"{ApiPaths.JoinRequests}/{requestId}", ct);
        await EvictAsync(ApiPaths.MyJoinRequests);
    }

    public Task<IReadOnlyList<PendingJoinRequest>> GetPendingJoinRequestsAsync(
        Guid organizationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PendingJoinRequest>>(ApiPaths.FamilyJoinRequests(organizationId), ct);

    public Task<IReadOnlyList<PendingJoinRequest>?> PeekPendingJoinRequestsAsync(
        Guid organizationId, CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<PendingJoinRequest>>(ApiPaths.FamilyJoinRequests(organizationId), ct);

    public async Task ApproveJoinRequestAsync(
        Guid organizationId, Guid requestId, ApproveJoinRequest decision, CancellationToken ct = default)
    {
        await SendNoDataAsync(
            HttpMethod.Post, $"{ApiPaths.FamilyJoinRequests(organizationId)}/{requestId}/approve", decision, ct);
        // Somebody new is in: the queue shrank, the roster grew, and — if the admin handed the
        // family over in the same act — the caller's own role in it changed.
        await EvictAsync(
            ApiPaths.FamilyJoinRequests(organizationId), ApiPaths.FamilyMembers(organizationId), ApiPaths.MyFamilies);
    }

    public async Task DeclineJoinRequestAsync(Guid organizationId, Guid requestId, CancellationToken ct = default)
    {
        await SendNoDataAsync(
            HttpMethod.Post, $"{ApiPaths.FamilyJoinRequests(organizationId)}/{requestId}/decline", ct);
        await EvictAsync(ApiPaths.FamilyJoinRequests(organizationId));
    }

    public async Task<CaregiverInviteResponse> CreateCaregiverInviteAsync(
        Guid cardiMemberId, CreateCaregiverInviteRequest request, CancellationToken ct = default)
    {
        var invite = await PostAsync<CreateCaregiverInviteRequest, CaregiverInviteResponse>(
            ApiPaths.CaregiverInvites(cardiMemberId), request, ct);
        await EvictAsync(ApiPaths.CaregiverInvites(cardiMemberId));
        return invite;
    }

    public Task<IReadOnlyList<CaregiverInviteResponse>> GetCaregiverInvitesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CaregiverInviteResponse>>(ApiPaths.CaregiverInvites(cardiMemberId), ct);

    public Task<IReadOnlyList<CaregiverInviteResponse>?> PeekCaregiverInvitesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<CaregiverInviteResponse>>(ApiPaths.CaregiverInvites(cardiMemberId), ct);

    public async Task<CaregiverInviteResponse> RevokeCaregiverInviteAsync(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct = default)
    {
        var invite = await SendAsync<CaregiverInviteResponse>(
            HttpMethod.Delete, $"{ApiPaths.CaregiverInvites(cardiMemberId)}/{inviteId}", ct);
        await EvictAsync(ApiPaths.CaregiverInvites(cardiMemberId));
        return invite;
    }

    public Task<CaregiverInviteView> ViewCaregiverInviteAsync(string token, CancellationToken ct = default) =>
        GetAsync<CaregiverInviteView>(ApiPaths.CaregiverInvite(token), ct, cache: false);

    public async Task<CaregiverInviteRedemption> AcceptCaregiverInviteAsync(string token, CancellationToken ct = default)
    {
        var redemption = await SendAsync<CaregiverInviteRedemption>(
            HttpMethod.Post, $"{ApiPaths.CaregiverInvite(token)}/accept", ct);
        // A new grant and, unless they were already in it, a new family: every list that says
        // who the caller may see is now short by one.
        await EvictAsync(ApiPaths.MyFamilies, ApiPaths.CardiMembers);
        return redemption;
    }

    public Task DeclineCaregiverInviteAsync(string token, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Post, $"{ApiPaths.CaregiverInvite(token)}/decline", ct);

    public Task ResendVerificationAsync(string email, CancellationToken ct = default) =>
        PostAsync<ResendVerificationRequest, bool>(
            "api/v1/auth/resend-verification", new ResendVerificationRequest { Email = email }, ct);

    // ---- Data-completeness notifications ----

    public Task<NotificationListResponse> GetNotificationsAsync(
        string? state = null,
        string? category = null,
        bool? owned = null,
        int? limit = null,
        CancellationToken ct = default) =>
        GetAsync<NotificationListResponse>(ApiPaths.Notifications(state, category, owned, limit), ct);

    public Task<NotificationListResponse?> PeekNotificationsAsync(
        string? state = null,
        string? category = null,
        bool? owned = null,
        int? limit = null,
        CancellationToken ct = default) =>
        PeekAsync<NotificationListResponse>(ApiPaths.Notifications(state, category, owned, limit), ct);

    public Task<NotificationSummaryResponse> GetNotificationSummaryAsync(CancellationToken ct = default) =>
        GetAsync<NotificationSummaryResponse>(ApiPaths.NotificationSummary, ct);

    public Task<NotificationSummaryResponse?> PeekNotificationSummaryAsync(CancellationToken ct = default) =>
        PeekAsync<NotificationSummaryResponse>(ApiPaths.NotificationSummary, ct);

    public async Task MarkNotificationSeenAsync(Guid notificationId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Post, $"api/v1/notifications/{notificationId}/seen", ct);
        await EvictAsync(NotificationKeys);
    }

    public async Task<NotificationResponse> SnoozeNotificationAsync(
        Guid notificationId, TimeSpan? duration = null, CancellationToken ct = default)
    {
        var snoozed = await PostAsync<SnoozeNotificationBody, NotificationResponse>(
            $"api/v1/notifications/{notificationId}/snooze",
            // Omitted rather than zero: the server falls back to the rule's own default, which is
            // the right answer when the user taps "not now" without picking a length.
            new SnoozeNotificationBody { Duration = duration?.ToString("c") },
            ct);
        await EvictAsync(NotificationKeys);
        return snoozed;
    }

    public async Task DismissNotificationAsync(
        Guid notificationId, bool acknowledgedConsequence = false, CancellationToken ct = default)
    {
        await SendNoDataAsync(
            HttpMethod.Post, $"api/v1/notifications/{notificationId}/dismiss",
            new DismissNotificationBody { AcknowledgedConsequence = acknowledgedConsequence },
            ct);
        await EvictAsync(NotificationKeys);
    }

    public Task<List<NotificationMuteResponse>> GetNotificationMutesAsync(CancellationToken ct = default) =>
        GetAsync<List<NotificationMuteResponse>>(ApiPaths.NotificationMutes, ct);

    public Task<List<NotificationMuteResponse>?> PeekNotificationMutesAsync(CancellationToken ct = default) =>
        PeekAsync<List<NotificationMuteResponse>>(ApiPaths.NotificationMutes, ct);

    public async Task RemoveNotificationMuteAsync(Guid muteId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/notifications/mutes/{muteId}", ct);
        await EvictAsync(NotificationKeys);
    }

    public async Task ResetNotificationMutesAsync(CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Post, "api/v1/notifications/mutes/reset", ct);
        await EvictAsync(NotificationKeys);
    }

    /// <summary>
    /// What any change to a notification makes stale: the open inbox (the only list the app
    /// reads — NotificationsPage asks for state=Open), the badge summary, and the mutes a
    /// snooze or dismissal may have added to.
    /// </summary>
    private static readonly string[] NotificationKeys =
    [
        ApiPaths.Notifications("Open", null, null, null),
        ApiPaths.NotificationSummary,
        ApiPaths.NotificationMutes,
    ];

    public Task UpdateTimeZoneAsync(string timeZoneId, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Put, "api/v1/users/me/timezone",
            new UpdateTimeZoneBody { TimeZoneId = timeZoneId }, ct);

    public Task<AccountDeletionStatusResponse> GetAccountDeletionAsync(CancellationToken ct = default) =>
        // Never cached, for the same reason the health-data disclosure is not: a stale "not
        // requested" would hide a deletion made on another device, and a stale "pending" would
        // offer to cancel something already cancelled. This answer decides whether the app
        // works at all, so it is always asked live.
        GetAsync<AccountDeletionStatusResponse>("api/v1/users/me/deletion", ct, cache: false);

    public Task<AccountDeletionStatusResponse> RequestAccountDeletionAsync(CancellationToken ct = default) =>
        SendAsync<AccountDeletionStatusResponse>(HttpMethod.Post, "api/v1/users/me/deletion", ct);

    public Task<AccountDeletionStatusResponse> CancelAccountDeletionAsync(CancellationToken ct = default) =>
        SendAsync<AccountDeletionStatusResponse>(HttpMethod.Delete, "api/v1/users/me/deletion", ct);

    public Task<HealthDataDisclosureResponse> GetHealthDataDisclosureAsync(CancellationToken ct = default) =>
        GetAsync<HealthDataDisclosureResponse>(ApiPaths.HealthDataDisclosure, ct, cache: false);

    public async Task DismissHealthDataDisclosureAsync(CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Post, ApiPaths.HealthDataDisclosure + "/dismiss", ct);
        // Not cached on read, but nothing may ever peek a pre-dismissal answer either.
        await EvictAsync(ApiPaths.HealthDataDisclosure);
    }

    public Task<PushDeviceTokenResponse> RegisterPushDeviceAsync(
        RegisterPushDeviceRequest request, CancellationToken ct = default) =>
        PostAsync<RegisterPushDeviceRequest, PushDeviceTokenResponse>("api/v1/notifications/devices", request, ct);

    public Task UnregisterPushDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Delete, "api/v1/notifications/devices",
            new UnregisterPushDeviceRequest { DeviceId = deviceId }, ct);

    // The background push handler's ack. A successful ack that throws here reads as a failed
    // delivery, which is exactly what escalation keys off (§7.2 C3) — so this one must not
    // trip over the message-only envelope the endpoint returns.
    public Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Post, $"api/v1/notifications/{deliveryId}/delivered",
            new AckDeliveryRequest { AckToken = ackToken }, ct);

    public Task<NotificationPreferenceResponse> GetNotificationPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<NotificationPreferenceResponse>(ApiPaths.NotificationPreferences, ct);

    public Task<NotificationPreferenceResponse?> PeekNotificationPreferencesAsync(CancellationToken ct = default) =>
        PeekAsync<NotificationPreferenceResponse>(ApiPaths.NotificationPreferences, ct);

    public async Task<NotificationPreferenceResponse> UpdateNotificationPreferencesAsync(
        UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        var preferences = await SendAsync<UpdateNotificationPreferenceRequest, NotificationPreferenceResponse>(
            HttpMethod.Put, ApiPaths.NotificationPreferences, request, ct);
        await EvictAsync(ApiPaths.NotificationPreferences);
        return preferences;
    }

    private sealed class SnoozeNotificationBody
    {
        public string? Duration { get; set; }
    }

    private sealed class DismissNotificationBody
    {
        public bool AcknowledgedConsequence { get; set; }
    }

    private sealed class UpdateTimeZoneBody
    {
        public string? TimeZoneId { get; set; }
    }

    public CacheOrigin? OriginOf(Task call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _origins.TryGetValue(call, out var origin) ? origin : null;
    }

    /// <summary>
    /// Starts the call and files its origin under the task it returns, before handing that same
    /// task back. Not itself async: the task has to exist before it can be a key, which it cannot
    /// inside the method that produces it.
    /// </summary>
    private Task<T> GetAsync<T>(
        string path, CancellationToken ct, bool allowNullData = false, bool cache = true)
    {
        var origin = new CacheOrigin();
        var call = GetCoreAsync<T>(path, origin, allowNullData, ct, cache);
        _origins.AddOrUpdate(call, origin);
        return call;
    }

    /// <summary>
    /// The cache-only twin of <see cref="GetAsync{T}"/>: the device's last answer to exactly
    /// this path, or null, without going near the network. Its origin is filed the same way, so
    /// <see cref="OriginOf"/> on the returned task says when the snapshot was saved — which is
    /// what a screen showing it needs to say out loud.
    /// </summary>
    private Task<T?> PeekAsync<T>(string path, CancellationToken ct)
    {
        var origin = new CacheOrigin();
        var call = TryReadCacheAsync<T>(path, origin, ct);
        _origins.AddOrUpdate(call, origin);
        return call;
    }

    private async Task<T> GetCoreAsync<T>(
        string path, CacheOrigin origin, bool allowNullData, CancellationToken ct, bool cache)
    {
        // Captured before the network call: a response that outlives this session must not
        // land in the next caregiver's cache. A non-null token at save time is not enough —
        // the next session may already be signed in.
        var generation = _session?.Current ?? 0;

        // And the same guard against a mutation rather than a sign-out. Pausing monitoring evicts
        // this member's stored interpretations because they stop being true; a read that started
        // before the pause and returns after it would put the pre-pause narrative straight back,
        // and the Journal would show it for monitoring that has stopped. Checking the token at
        // save time cannot catch this — the request was never cancelled, it simply began in a
        // world the mutation has since left.
        //
        // Per key, not a count of all evictions. A 404 evicts the path it was asked for, and the
        // warmer fetches a member's screens together — so one expected miss among them would
        // otherwise bar every other read in the batch from caching and quietly undo the warm.
        var evictions = EvictionsOf(path);
        var epoch = Volatile.Read(ref _cacheEpoch);

        // This read's place in the order, taken before the request goes out.
        var read = _writeOrder.Begin();

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw NetworkError("GET", path, ex, ct);

            if (cache && await TryReadCacheAsync<T>(path, origin, ct) is { } cached)
                return cached;

            throw NetworkError("GET", path, ex, ct);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The server says it is gone. A snapshot that outlived it would be served on the
            // next landing as if it were still there — a removed member's dashboard, a deleted
            // alert — so the device forgets it in the same breath.
            await EvictAsync(path);
            throw await MapErrorAsync("GET", path, response, ct);
        }

        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync("GET", path, response, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        var value = UnwrapEnvelope<T>("GET", path, body, response.StatusCode, allowNullData);
        // A null-data success is an answer, but not one worth caching: TryReadCacheAsync would
        // only reject the entry as unreadable on the way back out, one warning per offline read.
        if (value is not null && cache && EvictionsOf(path) == evictions
            && Volatile.Read(ref _cacheEpoch) == epoch)
        {
            // Ordered against every other read of this key, this client's and any other's —
            // see CacheWriteOrder.
            await _writeOrder.WriteAsync(path, read, () => TrySaveCacheAsync(path, body, generation, ct));
        }
        return value;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, body, Json, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError("POST", path, ex, ct);
        }
        return await ReadEnvelopeAsync<TResponse>("POST", path, response, ct);
    }

    /// <summary>PUT/DELETE/bodyless-POST returning the standard envelope.</summary>
    private Task<TResponse> SendAsync<TResponse>(HttpMethod method, string path, CancellationToken ct) =>
        SendAsync<object?, TResponse>(method, path, body: null, ct);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await SendCoreAsync(method, path, body, ct, timeout);
        return await ReadEnvelopeAsync<TResponse>(method.Method, path, response, ct);
    }

    /// <summary>
    /// For commands whose success is the status code: 204s, and the message-only 200 envelope
    /// (<c>{ success, message, timestamp }</c> with no <c>data</c>) the API returns from a command
    /// that has nothing to hand back. There is no payload to unwrap, so reading one would only
    /// invent failures — <see cref="ReadEnvelopeAsync"/> rejects a null <c>data</c>, which is the
    /// right call for endpoints that do return something and the wrong one here.
    /// Failures still surface: any non-2xx goes through <see cref="MapErrorAsync"/> as usual.
    /// </summary>
    private async Task SendNoDataAsync<TRequest>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct)
    {
        var response = await SendCoreAsync(method, path, body, ct);
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(method.Method, path, response, ct);
    }

    private Task SendNoDataAsync(HttpMethod method, string path, CancellationToken ct) =>
        SendNoDataAsync<object?>(method, path, body: null, ct);

    private async Task<HttpResponseMessage> SendCoreAsync<TRequest>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (timeout is { } perRequest)
            request.Options.Set(TimeoutHandler.TimeoutOption, perRequest);
        if (body is not null)
        {
            // JsonContent re-serializes on each read, so the auth handler's 401 retry can
            // re-send this request — same reason PostAsJsonAsync is used above.
            request.Content = JsonContent.Create(body, mediaType: null, Json);
        }

        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(method.Method, path, ex, ct);
        }
    }

    private async Task<T> ReadEnvelopeAsync<T>(string method, string path, HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(method, path, response, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        return UnwrapEnvelope<T>(method, path, body, response.StatusCode);
    }

    private T UnwrapEnvelope<T>(string method, string path, string body, HttpStatusCode statusCode, bool allowNullData = false)
    {
        var parsed = JsonUtility.TryDeserialize<ApiResponse<T>>(body, out var envelope, out var jsonErrors);
        if (!parsed || envelope!.Data is null)
        {
            // Some endpoints answer a question with "there isn't one" as a successful envelope
            // whose data is null — e.g. member-chat's sessions/current when no conversation
            // exists yet. For those callers a readable success envelope with no data is an
            // answer, not a fault; an unreadable body still is one.
            if (allowNullData && parsed && envelope!.Success)
                return default!;

            _logger.LogError("API {Method} {Path} returned {StatusCode} with an empty or unreadable envelope: {JsonErrors}. Payload: {Payload}",
                method, path, (int)statusCode,
                jsonErrors.Count == 0 ? "no data in envelope" : string.Join("; ", jsonErrors),
                JsonUtility.PreviewOf(body));
            throw new ApiException(statusCode, "The server returned an empty response.");
        }
        return envelope.Data;
    }

    private async Task<T?> TryReadCacheAsync<T>(string path, CacheOrigin origin, CancellationToken ct)
    {
        if (_cache is null)
            return default;

        OfflineCacheEntry? entry;
        try
        {
            entry = await _cache.TryGetAsync(path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up on this read — a chip tap superseding a load, a screen going
            // away. That is not a failed cache and must not be logged as one; the caller's own
            // cancellation handling is the right place for it to land.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache read failed for GET {Path}", path);
            return default;
        }

        if (entry is null)
            return default;

        if (!JsonUtility.TryDeserialize<ApiResponse<T>>(entry.Payload, out var envelope, out _)
            || envelope!.Data is null)
        {
            _logger.LogWarning("Offline cache entry for GET {Path} was unreadable; ignoring it", path);
            return default;
        }

        origin.CachedAt = entry.CachedAt;
        _logger.LogInformation("Serving GET {Path} from the on-device cache (saved {CachedAt:o})",
            path, entry.CachedAt);
        return envelope.Data;
    }

    private async Task TrySaveCacheAsync(string path, string body, int generation, CancellationToken ct)
    {
        if (_cache is null)
            return;

        // Belt-and-suspenders with SameSession: a token that is already gone is a
        // session that has ended, even if the generation counter was not wired in.
        if (_tokens is not null && await _tokens.GetAsync() is null)
            return;

        // Recheck after those awaits: sign-out + the next sign-in can land between
        // "generation still matches" and the write, and a non-null token is then the
        // new caregiver's, not this GET's.
        if (!SameSession(generation))
            return;

        try
        {
            await _cache.SaveAsync(path, body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache write failed for GET {Path}", path);
        }
    }

    private bool SameSession(int generation) =>
        _session is null || _session.Current == generation;

    /// <summary>
    /// Drops the snapshots a successful mutation has made stale. Best-effort and uncancellable:
    /// the server has already changed, so a screen's cancel after the fact must not leave the
    /// device holding the old answer, and a cache that cannot delete must not turn a mutation
    /// that succeeded into one that appears to have failed.
    /// </summary>
    /// <summary>
    /// Drops every saved read on the device, and bars the reads already in flight from putting
    /// theirs back.
    /// </summary>
    /// <remarks>
    /// The eviction counter is bumped for the same reason <see cref="EvictAsync"/> bumps a key's:
    /// a GET that started before this ran holds a generation from before it, and
    /// <see cref="TrySaveCacheAsync"/> refuses to write anything whose generation is stale. Tied
    /// to no key in particular, so it has to be every key — which is what
    /// <see cref="_evictions"/>'s null entry means.
    /// </remarks>
    private async Task ClearCacheAsync()
    {
        Interlocked.Increment(ref _cacheEpoch);
        if (_cache is null)
            return;

        try
        {
            await _cache.ClearAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Best-effort, like every other cache write: a device that will not let go of its
            // cache must not fail the leave that has already happened on the server.
            _logger.LogWarning(ex, "Clearing the offline cache after leaving a family failed.");
        }
    }

    private async Task EvictAsync(params string[] keys)
    {
        // Counted per key, before the removals: a read that returns while this is still deleting
        // has also raced the mutation and must not be allowed to save either.
        foreach (var key in keys)
            _evictions.AddOrUpdate(key, 1, static (_, count) => count + 1);

        if (_cache is null)
            return;

        foreach (var key in keys)
        {
            try
            {
                await _cache.RemoveAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Offline cache eviction failed for GET {Path}", key);
            }
        }
    }

    // ---- Health data export (M1-17, Story 6.3) ----

    public async Task<ExportConsentResponse> RecordExportConsentAsync(
        RecordExportConsentRequest request, CancellationToken ct = default)
    {
        var recorded = await PostAsync<RecordExportConsentRequest, ExportConsentResponse>(
            "api/v1/reports/consent", request, ct);
        await EvictAsync(ApiPaths.ExportConsents);
        return recorded;
    }

    public async Task<ExportConsentResponse> ReuseExportConsentAsync(
        Guid consentId, GenerateReportRequest request, CancellationToken ct = default)
    {
        var recorded = await PostAsync<GenerateReportRequest, ExportConsentResponse>(
            $"api/v1/reports/consents/{consentId}/reuse", request, ct);
        await EvictAsync(ApiPaths.ExportConsents);
        return recorded;
    }

    public Task<List<ExportConsentHistoryItem>> GetExportConsentsAsync(
        CancellationToken ct = default) =>
        GetAsync<List<ExportConsentHistoryItem>>(ApiPaths.ExportConsents, ct);

    public Task<List<ExportConsentHistoryItem>?> PeekExportConsentsAsync(
        CancellationToken ct = default) =>
        PeekAsync<List<ExportConsentHistoryItem>>(ApiPaths.ExportConsents, ct);

    public async Task RevokeExportConsentAsync(Guid consentId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/reports/consents/{consentId}", ct);
        await EvictAsync(ApiPaths.ExportConsents);
    }

    public Task<ReportQueuedResponse> GenerateReportAsync(
        GenerateReportRequest request, CancellationToken ct = default) =>
        PostAsync<GenerateReportRequest, ReportQueuedResponse>("api/v1/reports", request, ct);

    public async Task<ReportStatusResponse?> GetReportStatusAsync(
        string reportId, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<ReportStatusResponse>($"api/v1/reports/{reportId}", ct);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            // Unknown, expired, or another user's — indistinguishable by design, and all three
            // mean the same thing to a poller: there is nothing here any more.
            return null;
        }
    }

    public async Task<ReportFile> DownloadReportAsync(string reportId, CancellationToken ct = default)
    {
        const string path = "api/v1/reports/{0}/download";
        var requestPath = string.Format(CultureInfo.InvariantCulture, path, reportId);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(requestPath, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError("GET", requestPath, ex, ct);
        }

        // Disposed, unlike the JSON calls above: this is the one response in the client that
        // carries a whole file, so holding its content stream holds a pooled connection open for
        // as long as the caller keeps the ReportFile.
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await MapErrorAsync("GET", requestPath, response, ct);

            // Raw bytes, not the JSON envelope every other call unwraps — this endpoint serves a
            // file. The filename comes from Content-Disposition because the server built it from
            // the member and period, and the client should not try to reconstruct that.
            var content = await response.Content.ReadAsByteArrayAsync(ct);

            // ToString(), not MediaType: the header is "text/csv; charset=utf-8" and MediaType
            // drops the charset — the very thing the CSV renderer's BOM exists to get right. This
            // value is handed to the OS share sheet, so it should say everything the server said.
            var contentType = response.Content.Headers.ContentType?.ToString()
                ?? "application/octet-stream";

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"carditrack-export-{reportId}";

            return new ReportFile(content, contentType, fileName);
        }
    }

    private async Task<ApiException> MapErrorAsync(string method, string path, HttpResponseMessage response, CancellationToken ct)
    {
        string message = $"Request failed ({(int)response.StatusCode}).";
        string? traceId = null;
        List<string>? errors = null;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (JsonUtility.TryDeserialize<ErrorResponse>(body, out var error, out var bodyJsonErrors))
        {
            if (!string.IsNullOrWhiteSpace(error!.Message))
                message = error.Message;
            traceId = error.TraceId;
            if (error.Errors is { Count: > 0 })
                errors = error.Errors.Select(e => $"{e.Field}: {e.Message}".TrimStart(' ', ':')).ToList();
        }
        else
        {
            _logger.LogDebug("API {Method} {Path} error body was not a parseable ErrorResponse: {JsonErrors}. Payload: {Payload}",
                method, path, string.Join("; ", bodyJsonErrors), JsonUtility.PreviewOf(body));
        }

        // TraceId ties this entry to the server-side Serilog entry for the same request.
        var level = (int)response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(level, "API {Method} {Path} failed with {StatusCode}: {ServerMessage} (TraceId: {TraceId})",
            method, path, (int)response.StatusCode, message, traceId);

        return new ApiException(response.StatusCode, message, errors);
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException;

    private ApiException NetworkError(string method, string path, Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            _logger.LogDebug("API {Method} {Path} was canceled by the caller", method, path);
        else
            _logger.LogError(ex, "API {Method} {Path} failed with a transport error", method, path);

        // TimeoutHandler's expiry means the server was reached but too slow — "check your
        // internet" would send the caregiver chasing the wrong problem.
        return ex is TimeoutException
            ? new(HttpStatusCode.RequestTimeout, "The server is taking too long to answer. Try again in a moment.", inner: ex)
            : new(HttpStatusCode.ServiceUnavailable, "No connection. Check your internet and try again.", inner: ex);
    }
}
