using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using CardiTrack.UnitTests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container: the noise gates the digest job relies on are indexed
/// queries, the encrypted columns are deliberately unbounded, and the delete has to be a real one.
/// A substitute could vouch for none of that.
/// </summary>
[Collection("DatabaseCollection")]
public class MemberQuestionnaireRepositoryTests(TestDatabaseFixture fixture)
{
    private static MemberQuestionnaire Questionnaire(
        Guid memberId, DateTime generatedAtUtc, QuestionnaireStatus status = QuestionnaireStatus.Pending,
        string question = "Has anything changed at home recently?", string? answer = null,
        DateTime? askableUntilUtc = null) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = memberId,
            QuestionText = question,
            AnswerText = answer,
            TriggerContext = "Sleep has been shorter all week.",
            Status = status,
            GeneratedAtUtc = generatedAtUtc,
            AskableUntilUtc = askableUntilUtc,
        };

    private static async Task<IMemberQuestionnaireRepository> SaveAsync(
        IServiceScope scope, params MemberQuestionnaire[] questionnaires)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IMemberQuestionnaireRepository>();
        foreach (var questionnaire in questionnaires)
            await repo.AddAsync(questionnaire);

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return repo;
    }

    /// <summary>
    /// The columns hold ciphertext, which is longer than what it encrypts and carries a key
    /// fingerprint on top — a varchar sized to the plaintext would reject values the service
    /// considers valid.
    /// </summary>
    [Fact]
    public async Task StoresTheEncryptedEnvelope_WhateverItsLength()
    {
        using var scope = fixture.CreateScope();
        var memberId = Guid.NewGuid();
        var encryption = PromptContextFactory.Encryption;
        var question = "Has anything changed at home recently?";
        var answer = new string('a', 2_000);

        var questionnaire = Questionnaire(
            memberId, DateTime.UtcNow, QuestionnaireStatus.Answered,
            encryption.Encrypt(question), encryption.Encrypt(answer));
        await SaveAsync(scope, questionnaire);

        using var reading = fixture.CreateScope();
        var stored = await reading.ServiceProvider.GetRequiredService<CardiTrack.Infrastructure.Persistence.CardiTrackDbContext>()
            .MemberQuestionnaires.AsNoTracking().SingleAsync(q => q.Id == questionnaire.Id);

        Assert.StartsWith("v1:", stored.QuestionText);
        Assert.Equal(question, encryption.Decrypt(stored.QuestionText));
        Assert.Equal(answer, encryption.Decrypt(stored.AnswerText!));
        Assert.Equal(QuestionnaireStatus.Answered, stored.Status);
    }

    [Fact]
    public async Task GetByCardiMemberAsync_ReturnsEveryStatus_NewestFirst()
    {
        using var scope = fixture.CreateScope();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo = await SaveAsync(scope,
            Questionnaire(memberId, now.AddDays(-30), QuestionnaireStatus.Dismissed, question: "Oldest?"),
            Questionnaire(memberId, now.AddDays(-10), QuestionnaireStatus.Answered, question: "Middle?", answer: "Yes."),
            Questionnaire(memberId, now, question: "Newest?"),
            Questionnaire(Guid.NewGuid(), now, question: "Someone else?"));

        var all = await repo.GetByCardiMemberAsync(memberId);

        Assert.Equal(["Newest?", "Middle?", "Oldest?"], all.Select(q => q.QuestionText));
    }

    [Fact]
    public async Task HasPendingAsync_IsTrueOnlyWhileAQuestionIsWaiting()
    {
        using var scope = fixture.CreateScope();
        var waiting = Guid.NewGuid();
        var settled = Guid.NewGuid();

        var repo = await SaveAsync(scope,
            Questionnaire(waiting, DateTime.UtcNow),
            Questionnaire(settled, DateTime.UtcNow, QuestionnaireStatus.Answered, answer: "Yes."),
            Questionnaire(settled, DateTime.UtcNow.AddDays(-9), QuestionnaireStatus.Dismissed));

        Assert.True(await repo.HasPendingAsync(waiting, DateTime.UtcNow));
        Assert.False(await repo.HasPendingAsync(settled, DateTime.UtcNow));
        Assert.False(await repo.HasPendingAsync(Guid.NewGuid(), DateTime.UtcNow));
    }

    /// <summary>
    /// A question nobody got to before its day ended is not a question this family is being asked.
    /// Without this, one lapsed row would gag the feature for that member until the sweep next ran.
    /// </summary>
    [Fact]
    public async Task HasPendingAsync_IgnoresAQuestionThatOutlivedItsDay()
    {
        using var scope = fixture.CreateScope();
        var lapsed = Guid.NewGuid();
        var live = Guid.NewGuid();
        var timeless = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var repo = await SaveAsync(scope,
            Questionnaire(lapsed, now.AddDays(-1), askableUntilUtc: now.AddHours(-5)),
            Questionnaire(live, now.AddHours(-2), askableUntilUtc: now.AddHours(5)),
            // A standing-fact question, and every row written before questions carried a deadline.
            Questionnaire(timeless, now.AddHours(-2), askableUntilUtc: null));

        Assert.False(await repo.HasPendingAsync(lapsed, now));
        Assert.True(await repo.HasPendingAsync(live, now));
        Assert.True(await repo.HasPendingAsync(timeless, now));
    }

    /// <summary>What the expiry sweep retires, across every member rather than one.</summary>
    /// <remarks>
    /// This query is fleet-wide by design, so the shared container's other rows are in scope for
    /// it. The assertions are therefore about this test's own five rows: which of them came back,
    /// not how many rows exist.
    /// </remarks>
    [Fact]
    public async Task GetLapsedPendingAsync_FindsOnlyWaitingQuestionsPastTheirDay()
    {
        using var scope = fixture.CreateScope();
        var now = DateTime.UtcNow;

        var lapsedPending = Questionnaire(Guid.NewGuid(), now.AddDays(-1), askableUntilUtc: now.AddHours(-5));
        var stillAskable = Questionnaire(Guid.NewGuid(), now.AddHours(-2), askableUntilUtc: now.AddHours(5));
        var noDeadline = Questionnaire(Guid.NewGuid(), now.AddHours(-2), askableUntilUtc: null);
        // Settled rows are nobody's to retire, however far past their day they sit.
        var answered = Questionnaire(Guid.NewGuid(), now.AddDays(-9), QuestionnaireStatus.Answered,
            answer: "Yes.", askableUntilUtc: now.AddDays(-8));
        var dismissed = Questionnaire(Guid.NewGuid(), now.AddDays(-9), QuestionnaireStatus.Dismissed,
            askableUntilUtc: now.AddDays(-8));

        var repo = await SaveAsync(scope, lapsedPending, stillAskable, noDeadline, answered, dismissed);

        var found = (await repo.GetLapsedPendingAsync(now, limit: 1_000)).Select(q => q.Id).ToHashSet();

        Assert.Contains(lapsedPending.Id, found);
        Assert.DoesNotContain(stillAskable.Id, found);
        Assert.DoesNotContain(noDeadline.Id, found);
        Assert.DoesNotContain(answered.Id, found);
        Assert.DoesNotContain(dismissed.Id, found);
    }

    /// <summary>
    /// A bounded batch, so one long outage's backlog does not arrive as a single unbounded write —
    /// oldest first, so nothing starves behind a steady trickle of newer lapses.
    /// </summary>
    [Fact]
    public async Task GetLapsedPendingAsync_TakesTheOldestUpToTheLimit()
    {
        using var scope = fixture.CreateScope();
        var now = DateTime.UtcNow;

        var repo = await SaveAsync(scope, Enumerable.Range(1, 5)
            .Select(i => Questionnaire(
                Guid.NewGuid(), now.AddDays(-i), askableUntilUtc: now.AddHours(-i)))
            .ToArray());

        var lapsed = await repo.GetLapsedPendingAsync(now, limit: 2);

        Assert.Equal(2, lapsed.Count);
        Assert.True(lapsed[0].AskableUntilUtc <= lapsed[1].AskableUntilUtc);
    }

    /// <summary>
    /// Measured across every status: declining to answer must not read as an invitation to ask
    /// again tomorrow.
    /// </summary>
    [Fact]
    public async Task GetLatestGeneratedAtAsync_SpansEveryStatus_AndIsNullWhenNeverAsked()
    {
        using var scope = fixture.CreateScope();
        var memberId = Guid.NewGuid();
        var newest = DateTime.UtcNow.AddDays(-2);

        var repo = await SaveAsync(scope,
            Questionnaire(memberId, DateTime.UtcNow.AddDays(-20), QuestionnaireStatus.Answered, answer: "Yes."),
            Questionnaire(memberId, newest, QuestionnaireStatus.Dismissed));

        var latest = await repo.GetLatestGeneratedAtAsync(memberId);

        Assert.NotNull(latest);
        Assert.Equal(newest, latest.Value, TimeSpan.FromSeconds(1));
        Assert.Null(await repo.GetLatestGeneratedAtAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// A real row delete, not a flag. This entity is deliberately not soft-deletable: the answer is
    /// something a family member wrote about a person who never signed up to the service, so
    /// erasure has to mean gone (GDPR Art. 17).
    /// </summary>
    [Fact]
    public async Task Remove_LeavesNoRowBehind()
    {
        using var scope = fixture.CreateScope();
        var memberId = Guid.NewGuid();
        var questionnaire = Questionnaire(memberId, DateTime.UtcNow, QuestionnaireStatus.Answered, answer: "Yes.");

        var repo = await SaveAsync(scope, questionnaire);

        var tracked = await repo.GetByIdAsync(questionnaire.Id);
        repo.Remove(tracked!);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        using var reading = fixture.CreateScope();
        var remaining = await reading.ServiceProvider
            .GetRequiredService<CardiTrack.Infrastructure.Persistence.CardiTrackDbContext>()
            .MemberQuestionnaires.AsNoTracking().CountAsync(q => q.CardiMemberId == memberId);

        Assert.Equal(0, remaining);
    }
}
