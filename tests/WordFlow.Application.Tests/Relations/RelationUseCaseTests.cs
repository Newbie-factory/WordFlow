using System.Data.Common;
using WordFlow.Application.Ports;
using WordFlow.Application.Relations;

namespace WordFlow.Application.Tests.Relations;

public sealed class RelationUseCaseTests
{
    [Fact]
    public async Task No_relations_returns_a_successful_empty_result()
    {
        var source = Word(1, "plain");
        var vocabulary = new FakeVocabulary([source], []);
        var relations = new FakeRelations([]);

        var synonyms = await new GetSynonyms(relations, vocabulary, TimeProvider.System)
            .HandleAsync(new(source.WordId), default);
        var confusables = await new GetConfusables(relations, vocabulary, TimeProvider.System)
            .HandleAsync(new(source.WordId), default);

        Assert.Empty(Assert.IsType<Success<IReadOnlyList<SynonymGroup>>>(synonyms).Value);
        Assert.Empty(Assert.IsType<Success<IReadOnlyList<ConfusableItem>>>(confusables).Value);
    }

    [Fact]
    public async Task Synonyms_return_every_page_grouped_by_source_sense_and_POS_in_stable_order()
    {
        var source = Word(1, "run");
        var words = Enumerable.Range(1, 503).Select(i => Word(i, $"term-{i:000}")).ToArray();
        var relations = Enumerable.Range(2, 502).Select(i => new WordRelation(
            source.WordId, Id(i), RelationKinds.Synonym, RelationDirection.Bidirectional,
            i % 2 == 0 ? "sense-b" : "sense-a", $"target-{i}", i % 2 == 0 ? "v" : "n"))
            .Reverse().ToArray();
        var senses = new[]
        {
            new VocabularySense("sense-a", source.WordId, "noun sense", "n"),
            new VocabularySense("sense-b", source.WordId, "verb sense", "v"),
        };

        var result = await new GetSynonyms(new FakeRelations(relations), new FakeVocabulary(words, senses), TimeProvider.System)
            .HandleAsync(new(source.WordId), default);

        var groups = Assert.IsType<Success<IReadOnlyList<SynonymGroup>>>(result).Value;
        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "sense-a", "sense-b" }, groups.Select(x => x.SourceSenseId));
        Assert.Equal(502, groups.Sum(x => x.Words.Count));
        Assert.All(groups, group => Assert.Equal(group.Words.OrderBy(x => x.Lemma, StringComparer.Ordinal).Select(x => x.WordId), group.Words.Select(x => x.WordId)));
    }

    [Fact]
    public async Task Confusables_return_all_verified_kinds_and_targeted_misspellings_without_flattening()
    {
        var source = Word(1, "stimulate");
        var target = Word(2, "stipulate");
        var repository = new FakeRelations([
            new(source.WordId, target.WordId, RelationKinds.SpellingSimilar, RelationDirection.Bidirectional),
            new(source.WordId, Id(3), RelationKinds.Synonym, RelationDirection.Bidirectional, "s", "t", "v"),
        ], [new("stimuate", source.WordId)]);
        var vocabulary = new FakeVocabulary([source, target, Word(3, "encourage")], []);

        var result = await new GetConfusables(repository, vocabulary, TimeProvider.System)
            .HandleAsync(new(source.WordId), default);

        var items = Assert.IsType<Success<IReadOnlyList<ConfusableItem>>>(result).Value;
        Assert.Collection(items,
            item => { Assert.Equal(RelationKinds.Misspelling, item.RelationType); Assert.Equal("stimuate", item.Spelling); },
            item => { Assert.Equal(RelationKinds.SpellingSimilar, item.RelationType); Assert.Equal(target.WordId, item.WordId); });
    }

    [Fact]
    public async Task Personal_override_can_add_or_suppress_and_is_directionally_exact()
    {
        var source = Word(1, "source");
        var target = Word(2, "target");
        var vocabulary = new FakeVocabulary([source, target], []);
        var repository = new FakeRelations([]);
        var update = new UpdatePersonalRelation(repository, vocabulary, TimeProvider.System);

        var added = await update.HandleAsync(new(source.WordId, target.WordId, RelationKinds.PersonalConfusable, true), default);
        var visible = await new GetConfusables(repository, vocabulary, TimeProvider.System).HandleAsync(new(source.WordId), default);
        var suppressed = await update.HandleAsync(new(source.WordId, target.WordId, RelationKinds.PersonalConfusable, false), default);
        var empty = await new GetConfusables(repository, vocabulary, TimeProvider.System).HandleAsync(new(source.WordId), default);

        Assert.IsType<Success<UserRelationOverride>>(added);
        Assert.Single(Assert.IsType<Success<IReadOnlyList<ConfusableItem>>>(visible).Value);
        Assert.IsType<Success<UserRelationOverride>>(suppressed);
        Assert.Empty(Assert.IsType<Success<IReadOnlyList<ConfusableItem>>>(empty).Value);
        Assert.Empty((await repository.GetRelationsAsync(target.WordId, new PageRequest(0, 500), default)).Items);
    }

    [Fact]
    public async Task Missing_words_storage_failures_cancellation_and_corruption_have_distinct_boundaries()
    {
        var vocabulary = new FakeVocabulary([], []);
        var repository = new FakeRelations([]);
        var handler = new GetSynonyms(repository, vocabulary, TimeProvider.System);
        Assert.IsType<NotFound<IReadOnlyList<SynonymGroup>>>(await handler.HandleAsync(new(Id(1)), default));

        vocabulary.Failure = new FakeDbException();
        Assert.IsType<StorageFailure<IReadOnlyList<SynonymGroup>>>(await handler.HandleAsync(new(Id(1)), default));

        vocabulary.Failure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(new(Id(1)), default));

        vocabulary.Failure = new InvalidDataException("corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(new(Id(1)), default));
    }

    [Theory]
    [InlineData("synonym")]
    [InlineData("misspelling")]
    [InlineData("not-a-real-kind")]
    public async Task Illegal_personal_relation_types_are_rejected_without_writing(string kind)
    {
        var vocabulary = new FakeVocabulary([Word(1, "a"), Word(2, "b")], []);
        var repository = new FakeRelations([]);
        var handler = new UpdatePersonalRelation(repository, vocabulary, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => handler.HandleAsync(new(Id(1), Id(2), kind, true), default));

        Assert.Empty(repository.Overrides);
    }

    private static VocabularyWord Word(int id, string lemma) => new(Id(id), lemma, id, true);
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private sealed class FakeDbException : DbException;

    private sealed class FakeVocabulary(IEnumerable<VocabularyWord> words, IEnumerable<VocabularySense> senses) : IVocabularyRepository
    {
        private readonly VocabularyWord[] allWords = words.ToArray();
        private readonly VocabularySense[] allSenses = senses.ToArray();
        public Exception? Failure { get; set; }
        public Task<Page<VocabularyWord>> GetWordsAsync(PageRequest page, CancellationToken ct) =>
            Task.FromResult(new Page<VocabularyWord>(allWords.Skip(page.Offset).Take(page.Limit).ToArray(), allWords.Length));
        public Task<VocabularyWord?> GetWordAsync(Guid wordId, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult(allWords.SingleOrDefault(x => x.WordId == wordId));
        }
        public Task<Page<VocabularySense>> GetSensesAsync(Guid wordId, PageRequest page, CancellationToken ct)
        {
            var matches = allSenses.Where(x => x.WordId == wordId).ToArray();
            return Task.FromResult(new Page<VocabularySense>(matches.Skip(page.Offset).Take(page.Limit).ToArray(), matches.Length));
        }
    }

    private sealed class FakeRelations(IEnumerable<WordRelation> builtIn, IEnumerable<MisspellingRelation>? misspellings = null) : IRelationRepository
    {
        private readonly WordRelation[] corpus = builtIn.ToArray();
        private readonly MisspellingRelation[] corrections = misspellings?.ToArray() ?? [];
        public List<UserRelationOverride> Overrides { get; } = [];

        public Task<Page<WordRelation>> GetRelationsAsync(Guid sourceWordId, PageRequest page, CancellationToken ct)
        {
            var merged = corpus.Where(x => x.SourceWordId == sourceWordId).ToDictionary(x => (x.TargetWordId, x.RelationType));
            foreach (var item in Overrides.Where(x => x.SourceWordId == sourceWordId))
            {
                if (item.IsEnabled) merged[(item.TargetWordId, item.RelationType)] = new(sourceWordId, item.TargetWordId, item.RelationType, RelationDirection.Forward);
                else merged.Remove((item.TargetWordId, item.RelationType));
            }
            var all = merged.Values.OrderBy(x => x.RelationType, StringComparer.Ordinal).ThenBy(x => x.TargetWordId).ToArray();
            return Task.FromResult(new Page<WordRelation>(all.Skip(page.Offset).Take(page.Limit).ToArray(), all.Length));
        }

        public Task<Page<MisspellingRelation>> GetMisspellingsAsync(Guid targetWordId, PageRequest page, CancellationToken ct)
        {
            var all = corrections.Where(x => x.TargetWordId == targetWordId).OrderBy(x => x.Spelling, StringComparer.Ordinal).ToArray();
            return Task.FromResult(new Page<MisspellingRelation>(all.Skip(page.Offset).Take(page.Limit).ToArray(), all.Length));
        }

        public Task SetOverrideAsync(UserRelationOverride relationOverride, CancellationToken ct)
        {
            Overrides.RemoveAll(x => x.SourceWordId == relationOverride.SourceWordId && x.TargetWordId == relationOverride.TargetWordId && x.RelationType == relationOverride.RelationType);
            Overrides.Add(relationOverride);
            return Task.CompletedTask;
        }
    }
}
