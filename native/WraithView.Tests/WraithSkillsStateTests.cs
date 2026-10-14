using WraithView.WraithSkills;
using Xunit;

namespace WraithView.Tests;

public class WraithSkillsStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wraithskills-tests", Guid.NewGuid().ToString("N"));

    public WraithSkillsStateTests() => WraithSkillsPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { WraithSkillsPaths.ClearOverride(); }
    }

    [Fact]
    public void RoundTripPreservesActiveTrainedQueueAndOwnerMetadata()
    {
        var state = new WraithSkillsState();
        var character = state.Upsert(9001);
        character.CharacterName = "Pilot";
        character.OwnerHash = "owner-hash";
        character.ActiveLevels[100] = 3;
        character.TrainedLevels[100] = 5;
        character.Queue.Add(new QueueEntry(100, 5, null, null));
        Assert.True(state.TrySave(out var error), error);
        var loaded = WraithSkillsState.Load();
        var restored = Assert.Single(loaded.State.Characters);
        Assert.Equal(3, restored.ActiveLevels[100]);
        Assert.Equal(5, restored.TrainedLevels[100]);
        Assert.Equal("owner-hash", restored.OwnerHash);
        Assert.Single(restored.Queue);
    }

    [Fact]
    public void CorruptPrimaryIsPreservedAndBackupIsRecovered()
    {
        var state = new WraithSkillsState();
        state.Upsert(1).CharacterName = "First";
        Assert.True(state.TrySave(out _));
        state.Find(1)!.CharacterName = "Second";
        Assert.True(state.TrySave(out _));
        File.WriteAllText(WraithSkillsPaths.StatePath, "{not-json");
        var loaded = WraithSkillsState.Load();
        Assert.Contains("Recovered", loaded.Warning);
        Assert.Equal("First", Assert.Single(loaded.State.Characters).CharacterName);
        Assert.NotEmpty(Directory.GetFiles(_dir, "state.json.corrupt-*"));
    }

    [Fact]
    public void FetchFailureKeepsLastGoodDataAndOnlyDefinitiveFailureSetsReauth()
    {
        var state = new WraithSkillsState();
        var character = state.Upsert(42);
        state.ApplyFetchSuccess(42, new Dictionary<int, int> { [1] = 3 }, new Dictionary<int, int> { [1] = 5 }, [new QueueEntry(1, 5, null, null)], DateTimeOffset.UtcNow);
        state.ApplyFetchFailure(42, "temporary", needsReauth: false);
        Assert.Equal(3, character.ActiveLevels[1]);
        Assert.Equal(5, character.TrainedLevels[1]);
        Assert.False(character.NeedsReauth);
        state.ApplyFetchFailure(42, "invalid grant", needsReauth: true);
        Assert.True(character.NeedsReauth);
    }

    [Fact]
    public void NormalizeDropsInvalidRowsAndEnforcesCharacterCap()
    {
        var state = new WraithSkillsState
        {
            Characters = Enumerable.Range(0, WraithSkillsState.MaxCharacters + 10)
                .Select(index => new WraithSkillsCharacter { CharacterId = index, CharacterName = $" {index} " })
                .ToList(),
        };
        state.Normalize();
        Assert.Equal(WraithSkillsState.MaxCharacters, state.Characters.Count);
        Assert.DoesNotContain(state.Characters, character => character.CharacterId <= 0);
    }

    [Fact]
    public void CharacterOrderValidatesAndPersists()
    {
        var state = new WraithSkillsState();
        state.Upsert(1).CharacterName = "First";
        state.Upsert(2).CharacterName = "Second";
        state.Upsert(3).CharacterName = "Third";

        Assert.False(state.TryReorderCharacters([3, 3, 1]));
        Assert.Equal([1L, 2L, 3L], state.Characters.Select(character => character.CharacterId).ToArray());
        Assert.True(state.TryReorderCharacters([3, 1, 2]));
        Assert.True(state.TrySave(out var error), error);
        Assert.Equal([3L, 1L, 2L], WraithSkillsState.Load().State.Characters.Select(character => character.CharacterId).ToArray());
    }

    [Fact]
    public void PathsUseStandaloneWraithViewNamespace()
    {
        WraithSkillsPaths.ClearOverride();
        Assert.Contains(Path.Combine("WraithView", "WraithSkills"), WraithSkillsPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WraithHud", WraithSkillsPaths.Root, StringComparison.OrdinalIgnoreCase);
        WraithSkillsPaths.OverrideRoot(_dir);
    }
}
