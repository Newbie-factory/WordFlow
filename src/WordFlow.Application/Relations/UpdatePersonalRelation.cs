using WordFlow.Application.Ports;

namespace WordFlow.Application.Relations;

public sealed record UpdatePersonalRelationRequest(
    Guid SourceWordId,
    Guid TargetWordId,
    string RelationType,
    bool IsEnabled);

public sealed class UpdatePersonalRelation
{
    private readonly IRelationRepository relations;
    private readonly IVocabularyRepository vocabulary;
    private readonly TimeProvider timeProvider;

    public UpdatePersonalRelation(IRelationRepository relations, IVocabularyRepository vocabulary, TimeProvider timeProvider)
    {
        this.relations = relations ?? throw new ArgumentNullException(nameof(relations));
        this.vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<UseCaseResult<UserRelationOverride>> HandleAsync(UpdatePersonalRelationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RelationKinds.Confusable.Contains(request.RelationType))
            throw new ArgumentOutOfRangeException(nameof(request), request.RelationType, "Only confusable relation types can be overridden.");
        _ = timeProvider.GetUtcNow();
        try
        {
            if (await vocabulary.GetWordAsync(request.SourceWordId, ct).ConfigureAwait(false) is null)
                return new NotFound<UserRelationOverride>($"Source word {request.SourceWordId:D} was not found.");
            if (await vocabulary.GetWordAsync(request.TargetWordId, ct).ConfigureAwait(false) is null)
                return new NotFound<UserRelationOverride>($"Target word {request.TargetWordId:D} was not found.");
            var relationOverride = new UserRelationOverride(
                request.SourceWordId, request.TargetWordId, request.RelationType, request.IsEnabled, timeProvider.GetUtcNow());
            await relations.SetOverrideAsync(relationOverride, ct).ConfigureAwait(false);
            return new Success<UserRelationOverride>(relationOverride);
        }
        catch (TransientStorageException exception)
        {
            return new StorageFailure<UserRelationOverride>(exception.Message);
        }
    }
}
