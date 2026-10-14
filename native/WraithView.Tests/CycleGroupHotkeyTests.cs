using System.Text.Json.Nodes;
using WraithView.Preview;

namespace WraithView.Tests;

public class CycleGroupHotkeyTests
{
    [Fact]
    public void TwoEnabledGroupsPlanAllForwardAndBackwardHotkeys()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F15", "F16", "Support One"));

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Equal(new[] { "F13", "F14", "F15", "F16" }, registrations.Select(item => item.Gesture));
    }

    [Fact]
    public void PlannedCommandsCarryTheirGroupAndDirection()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F15", "F16", "Support One"));

        var registrations = Plan(profile, out _);

        Assert.Collection(
            registrations,
            item => AssertRegistration(item, "combat", 1),
            item => AssertRegistration(item, "combat", -1),
            item => AssertRegistration(item, "support", 1),
            item => AssertRegistration(item, "support", -1));
    }

    [Fact]
    public void CycleGroupsKeepIndependentCursorKeysAndAnchors()
    {
        var combatKey = WraithViewController.CycleCursorKey("profile", "combat");
        var supportKey = WraithViewController.CycleCursorKey("profile", "support");
        var cursors = new Dictionary<string, nint>
        {
            [combatKey] = (nint)11,
            [supportKey] = (nint)22,
        };

        var combatNext = WraithViewCycleState.NextIndex(new nint[] { 11, 12 }, 1, 0, 0, cursors[combatKey]);
        var supportNext = WraithViewCycleState.NextIndex(new nint[] { 21, 22 }, 1, 0, 0, cursors[supportKey]);

        Assert.NotEqual(combatKey, supportKey);
        Assert.Equal(1, combatNext);
        Assert.Equal(0, supportNext);
        Assert.Equal((nint)22, cursors[supportKey]);
    }

    [Fact]
    public void OverlappingGroupsRemainSeparateRegistrations()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Shared", "Combat One"),
            Group("Support", true, "F15", "F16", "Shared", "Support One"));

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Equal(2, registrations.Select(item => item.GroupId).Distinct().Count());
        Assert.Equal(4, registrations.Count);
    }

    [Fact]
    public void DisabledGroupDoesNotPlanHotkeys()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", false, "F15", "F16", "Support One"));

        var registrations = Plan(profile, out _);

        Assert.DoesNotContain(registrations, item => item.GroupId == "support");
        Assert.Equal(2, registrations.Count);
    }

    [Fact]
    public void ReenablingGroupRestoresItsBindings()
    {
        var support = Group("Support", false, "F15", "F16", "Support One");
        var profile = Profile(support);
        Assert.Empty(Plan(profile, out _));

        support.Enabled = true;
        var registrations = Plan(profile, out _);

        Assert.Equal(new[] { "F15", "F16" }, registrations.Select(item => item.Gesture));
    }

    [Fact]
    public void EditorSelectionDoesNotChangeRuntimeSignatureOrPlan()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F15", "F16", "Support One"));
        profile.SelectedCycleGroupId = "combat";
        var firstSignature = WraithViewOverlayForm.HotkeySignature(profile, Array.Empty<EveClientWindow>(), false);
        var firstPlan = Plan(profile, out _).Select(item => item.Gesture).ToArray();

        profile.SelectedCycleGroupId = "support";
        var secondSignature = WraithViewOverlayForm.HotkeySignature(profile, Array.Empty<EveClientWindow>(), false);
        var secondPlan = Plan(profile, out _).Select(item => item.Gesture).ToArray();

        Assert.Equal(firstSignature, secondSignature);
        Assert.Equal(firstPlan, secondPlan);
    }

    [Fact]
    public void EnabledStatesSurviveStructuredPatchAndSettingsReload()
    {
        var patchedGroups = WraithViewController.ParseCycleGroups(new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Combat",
                ["forwardGestures"] = new JsonArray("F13"),
                ["backwardGestures"] = new JsonArray("F14"),
                ["charactersText"] = "Combat One",
                ["enabled"] = false,
            },
        });
        var settings = new WraithViewSettings
        {
            SelectedProfileId = "default",
            Profiles = new List<WraithViewProfile>
            {
                new() { Id = "default", Name = "Default", CycleGroups = patchedGroups },
            },
        };

        var loaded = WraithViewSettings.FromJson(settings.ToJson());

        Assert.False(Assert.Single(loaded.ActiveProfile().CycleGroups).Enabled);
    }

    [Fact]
    public void LegacyGroupsWithoutEnabledPropertyDefaultToEnabled()
    {
        const string json = """
            {
              "selectedProfileId": "default",
              "profiles": [
                {
                  "id": "default",
                  "name": "Default",
                  "cycleGroups": [
                    {
                      "name": "Legacy",
                      "forwardGestures": ["F13"],
                      "backwardGestures": ["F14"],
                      "characters": ["Legacy One"]
                    }
                  ]
                }
              ]
            }
            """;

        var loaded = WraithViewSettings.FromJson(json);

        Assert.True(Assert.Single(loaded.ActiveProfile().CycleGroups).Enabled);
    }

    [Fact]
    public void DuplicateGestureKeepsFirstAndReportsConflictWithoutDroppingOthers()
    {
        var profile = Profile(
            Group("Combat", true, "F13", "F14", "Combat One"),
            Group("Support", true, "F13", "F16", "Support One"));

        var registrations = Plan(profile, out var failures);

        Assert.Equal(new[] { "F13", "F14", "F16" }, registrations.Select(item => item.Gesture));
        var failure = Assert.Single(failures);
        Assert.Contains("F13", failure);
        Assert.Contains("Support", failure);
        Assert.Contains("forward", failure);
        Assert.Contains("Combat", failure);
    }

    [Fact]
    public void EmptyGroupMembersFallBackToCharacterOrder()
    {
        var profile = Profile(Group("All", true, "F17", "F18"));
        profile.CharacterOrder = new List<string> { "One", "Two" };

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Equal(2, registrations.Count);
    }

    [Fact]
    public void ExistingSingleGroupBehaviorStillPlansBothDirections()
    {
        var profile = Profile(Group("All", true, "F13", "F14", "One", "Two"));

        var registrations = Plan(profile, out var failures);

        Assert.Empty(failures);
        Assert.Collection(
            registrations,
            item => AssertRegistration(item, "all", 1),
            item => AssertRegistration(item, "all", -1));
    }

    private static IReadOnlyList<WraithViewCycleHotkeyRegistration> Plan(
        WraithViewProfile profile,
        out List<string> failures)
    {
        failures = new List<string>();
        return WraithViewCycleHotkeyPlanner.Plan(profile, null, failures);
    }

    private static WraithViewProfile Profile(params WraithViewCycleGroup[] groups)
    {
        return new WraithViewProfile
        {
            Id = "profile",
            Name = "Profile",
            CycleGroups = groups.ToList(),
            SelectedCycleGroupId = groups.FirstOrDefault()?.Id ?? "",
        };
    }

    private static WraithViewCycleGroup Group(
        string name,
        bool enabled,
        string forward,
        string backward,
        params string[] characters)
    {
        return new WraithViewCycleGroup
        {
            Id = WraithViewCycleGroup.IdFromName(name),
            Name = name,
            Enabled = enabled,
            ForwardGestures = new List<string> { forward },
            BackwardGestures = new List<string> { backward },
            Characters = characters.ToList(),
        };
    }

    private static void AssertRegistration(WraithViewCycleHotkeyRegistration registration, string groupId, int direction)
    {
        Assert.Equal(groupId, registration.GroupId);
        Assert.Equal(direction, registration.Direction);
    }
}
