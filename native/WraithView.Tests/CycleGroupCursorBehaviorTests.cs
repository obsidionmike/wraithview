using WraithView.Preview;

namespace WraithView.Tests;

public class CycleGroupCursorBehaviorTests
{
    [Fact]
    public void RememberingPositionResumesPreviousGroupAfterCyclingAnotherGroup()
    {
        var groupA = Handles(1, 10);
        var groupB = Handles(15, 18);
        var active = (nint)9;

        active = groupB[WraithViewCycleState.NextIndex(groupB, 1, active, active, 0, true)];
        active = groupA[WraithViewCycleState.NextIndex(groupA, 1, active, active, 9, true)];

        Assert.Equal((nint)10, active);
    }

    [Fact]
    public void DisabledMemoryStartsForwardAtFirstMemberWhenEnteringGroup()
    {
        var groupA = Handles(1, 10);
        var groupB = Handles(15, 18);
        var active = (nint)9;

        active = groupB[WraithViewCycleState.NextIndex(groupB, 1, active, active, 0, false)];
        active = groupA[WraithViewCycleState.NextIndex(groupA, 1, active, active, 9, false)];

        Assert.Equal((nint)1, active);
    }

    [Fact]
    public void DisabledMemoryStartsBackwardAtLastMemberWhenEnteringGroup()
    {
        var groupA = Handles(1, 10);

        var next = WraithViewCycleState.NextIndex(groupA, -1, 15, 15, 9, false);

        Assert.Equal((nint)10, groupA[next]);
    }

    [Fact]
    public void DisabledMemoryStillCyclesRelativeToActiveMemberInsideGroup()
    {
        var groupA = Handles(1, 10);

        var next = WraithViewCycleState.NextIndex(groupA, 1, 9, 9, 1, false);

        Assert.Equal((nint)10, groupA[next]);
    }

    [Fact]
    public void TurningMemoryOffClearsEveryLogicalGroupCursor()
    {
        var profile = Profile(rememberPositions: true);
        var cursors = CursorMap();

        var changed = WraithViewController.ApplyRememberCycleGroupPositionsSetting(profile, false, cursors);

        Assert.True(changed);
        Assert.False(profile.RememberCycleGroupPositions);
        Assert.Empty(cursors);
    }

    [Fact]
    public void TurningMemoryBackOnDoesNotResurrectClearedPositions()
    {
        var profile = Profile(rememberPositions: true);
        var cursors = CursorMap();
        WraithViewController.ApplyRememberCycleGroupPositionsSetting(profile, false, cursors);
        cursors[WraithViewController.CycleCursorKey("profile", "stale")] = (nint)99;

        var changed = WraithViewController.ApplyRememberCycleGroupPositionsSetting(profile, true, cursors);

        Assert.True(changed);
        Assert.True(profile.RememberCycleGroupPositions);
        Assert.Empty(cursors);
    }

    [Fact]
    public void MissingRememberPositionPropertyDefaultsToEnabled()
    {
        const string json = """
            {
              "selectedProfileId": "default",
              "profiles": [
                {
                  "id": "default",
                  "name": "Default"
                }
              ]
            }
            """;

        var settings = WraithViewSettings.FromJson(json);

        Assert.True(settings.ActiveProfile().RememberCycleGroupPositions);
    }

    [Fact]
    public void ExplicitFalsePersistsAcrossSaveAndReload()
    {
        var settings = new WraithViewSettings
        {
            SelectedProfileId = "profile",
            Profiles = new List<WraithViewProfile> { Profile(rememberPositions: false) },
        };

        var loaded = WraithViewSettings.FromJson(settings.ToJson());

        Assert.False(loaded.ActiveProfile().RememberCycleGroupPositions);
    }

    [Fact]
    public void FailedActivationDoesNotEstablishOrAdvanceRememberedPosition()
    {
        var candidates = Handles(1, 10);
        var key = WraithViewController.CycleCursorKey("profile", "combat");
        var cursors = new Dictionary<string, nint> { [key] = (nint)9 };

        cursors[key] = (nint)10;
        cursors[key] = WraithViewCycleState.CursorAfterFailedActivation(candidates, (nint)9);
        Assert.Equal((nint)9, cursors[key]);

        cursors[key] = (nint)1;
        var restored = WraithViewCycleState.CursorAfterFailedActivation(candidates, nint.Zero);
        if (restored == nint.Zero) cursors.Remove(key);
        Assert.DoesNotContain(key, cursors.Keys);
    }

    private static nint[] Handles(int first, int last)
    {
        return Enumerable.Range(first, last - first + 1).Select(value => (nint)value).ToArray();
    }

    private static Dictionary<string, nint> CursorMap()
    {
        return new Dictionary<string, nint>
        {
            [WraithViewController.CycleCursorKey("profile", "combat")] = (nint)9,
            [WraithViewController.CycleCursorKey("profile", "support")] = (nint)15,
        };
    }

    private static WraithViewProfile Profile(bool rememberPositions)
    {
        return new WraithViewProfile
        {
            Id = "profile",
            Name = "Profile",
            RememberCycleGroupPositions = rememberPositions,
        };
    }
}
