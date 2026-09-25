using System.Collections.Generic;
using System.Linq;
using BlueStar.App.ViewModels;
using BlueStar.Core.Models;
using Xunit;

namespace BlueStar.App.Tests;

public class FilterGroupViewModelTests
{
    private static FilterOptionItem CreateOption(string key, string name, int tagId)
    {
        return new FilterOptionItem(
            new SteamFacetOption(SteamFacetKind.Tag, tagId.ToString(), name),
            name);
    }

    [Fact]
    public void FilterGroup_Default60Visible_ExpandsAndCollapsesWithToggleExpandAll()
    {
        var usage = new Dictionary<string, int>();
        var vm = new FilterGroupViewModel(
            key: "genre",
            title: "Genre",
            iconKey: "IconCategory",
            visibleCount: 60,
            isExpanded: true,
            usage: usage,
            onChanged: () => { });

        var options = Enumerable.Range(1, 100)
            .Select(i => CreateOption($"tag_{i}", $"Tag {i}", i))
            .ToList();

        vm.SetOptions(options);

        // By default, only visibleCount (60) are visible
        Assert.False(vm.IsExpandedAll);
        Assert.Equal(60, vm.Items.Count);
        Assert.Equal(40, vm.HiddenCount);

        // Toggle to expand all
        vm.ToggleExpandAllCommand.Execute(null);
        Assert.True(vm.IsExpandedAll);
        Assert.Equal(100, vm.Items.Count);
        Assert.Equal(0, vm.HiddenCount);

        // Toggle back to collapse
        vm.ToggleExpandAllCommand.Execute(null);
        Assert.False(vm.IsExpandedAll);
        Assert.Equal(60, vm.Items.Count);
        Assert.Equal(40, vm.HiddenCount);
    }

    [Fact]
    public void FilterGroup_HasRandomPick_TrueOnlyWhenMoreThan20Tags()
    {
        var usage = new Dictionary<string, int>();
        var vm = new FilterGroupViewModel(
            key: "features",
            title: "Features",
            iconKey: "IconSliders",
            visibleCount: 60,
            isExpanded: true,
            usage: usage,
            onChanged: () => { });

        // 20 options -> HasRandomPick must be false
        var options20 = Enumerable.Range(1, 20)
            .Select(i => CreateOption($"feat_{i}", $"Feature {i}", i))
            .ToList();
        vm.SetOptions(options20);
        Assert.False(vm.HasRandomPick);

        // 21 options -> HasRandomPick must become true
        var options21 = Enumerable.Range(1, 21)
            .Select(i => CreateOption($"feat_{i}", $"Feature {i}", i))
            .ToList();
        vm.SetOptions(options21);
        Assert.True(vm.HasRandomPick);
    }

    [Fact]
    public void FilterGroup_Highlight_BecomesActiveAt5Selections_AndDecaysAfter50OtherSelections()
    {
        var usage = new Dictionary<string, int>();
        var vm = new FilterGroupViewModel(
            key: "genre",
            title: "Genre",
            iconKey: "IconCategory",
            visibleCount: 80,
            isExpanded: true,
            usage: usage,
            onChanged: () => { });

        var options = Enumerable.Range(1, 100)
            .Select(i => CreateOption($"tag_{i}", $"Tag {i}", i))
            .ToList();

        vm.SetOptions(options);

        var target = options[0];

        // Select 4 times (Neutral -> Include)
        for (int i = 0; i < 4; i++)
        {
            target.State = FacetState.Neutral;
            vm.Toggle(target);
        }

        Assert.Equal(4, usage[target.Key]);
        Assert.False(target.IsHighlighted, "Should not be highlighted with only 4 selections");

        // 5th selection
        vm.Toggle(target);
        Assert.Equal(5, usage[target.Key]);
        Assert.True(target.IsHighlighted, "Must become highlighted when selected 5 times");

        // Now select other tags 50 times in this same category
        for (int i = 1; i <= 50; i++)
        {
            var other = options[i];
            vm.Toggle(other);
            Assert.True(target.IsHighlighted, $"Target should remain highlighted at offset {i} <= 50");
        }

        // 51st selection of another tag -> target must decay
        var nextOther = options[51];
        vm.Toggle(nextOther);
        Assert.False(target.IsHighlighted, "Target must decay and lose highlight after 51 other selections in category");
    }

    [Fact]
    public void FilterGroup_RestoreCategoryUsage_RestoresCountersAndReevaluatesHighlights()
    {
        var usage = new Dictionary<string, int>();
        var vm = new FilterGroupViewModel(
            key: "setting",
            title: "Setting",
            iconKey: "IconWorld",
            visibleCount: 80,
            isExpanded: true,
            usage: usage,
            onChanged: () => { });

        var options = Enumerable.Range(1, 20)
            .Select(i => CreateOption($"tag_{i}", $"Tag {i}", i))
            .ToList();

        var targetKey = options[0].Key;
        usage[targetKey] = 6; // 6 previous selections

        vm.SetOptions(options);

        // Restore category state: target was last selected at index 10, category counter is currently 25 (difference 15 <= 50)
        var lastSelected = new Dictionary<string, int> { [targetKey] = 10 };
        vm.RestoreCategoryUsage(counter: 25, lastSelectedAt: lastSelected);

        Assert.True(options[0].IsHighlighted);
        Assert.Equal(25, vm.CategorySelectionCounter);

        // If category counter was 65 (difference 55 > 50)
        vm.RestoreCategoryUsage(counter: 65, lastSelectedAt: lastSelected);
        Assert.False(options[0].IsHighlighted);
    }

    [Fact]
    public void FilterGroup_PickRandom_PicksFromAllAvailableOptions()
    {
        var usage = new Dictionary<string, int>();
        var vm = new FilterGroupViewModel(
            key: "pace",
            title: "Pace",
            iconKey: "IconClock",
            visibleCount: 80,
            isExpanded: true,
            usage: usage,
            onChanged: () => { });

        var options = Enumerable.Range(1, 100)
            .Select(i => CreateOption($"tag_{i}", $"Tag {i}", i))
            .ToList();

        vm.SetOptions(options);

        // Pre-activate first 80 options so only options beyond index 80 are neutral
        for (int i = 0; i < 80; i++)
        {
            options[i].State = FacetState.Include;
        }

        // PickRandom must be able to pick from the remaining neutral options (index 80..99)
        vm.PickRandomCommand.Execute(null);

        var activeBeyond80 = options.Skip(80).Count(o => o.State == FacetState.Include);
        Assert.Equal(1, activeBeyond80);
    }
}
