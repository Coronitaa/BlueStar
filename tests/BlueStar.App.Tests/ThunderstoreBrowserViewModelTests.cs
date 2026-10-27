using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Tasks;

using BlueStar.App.ViewModels;

using BlueStar.Core.Interfaces;

using BlueStar.Core.Models;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;



namespace BlueStar.App.Tests;



public sealed class ThunderstoreBrowserViewModelTests

{

    private readonly Mock<IThunderstoreService> _thunderstoreService = new();

    private ModPackage? _installedModPackage;



    private static ThunderstorePackage CreateSamplePackage(string name = "MoreCompany", string owner = "notnotnotswipez")

    {

        return new ThunderstorePackage

        {

            Name = name,

            FullName = $"{owner}-{name}",

            Owner = owner,

            PackageUrl = $"https://thunderstore.io/package/{owner}/{name}/",

            TotalDownloads = 1250000,

            RatingScore = 480,

            DateUpdated = DateTimeOffset.UtcNow,

            Categories = ["BepInEx", "Client", "Server"],

            Versions =

            [

                new ThunderstorePackageVersion

                {

                    Name = name,

                    FullName = $"{owner}-{name}-1.9.4",

                    VersionNumber = "1.9.4",

                    Description = "A mod that increases lobby player count.",

                    DownloadUrl = $"https://thunderstore.io/package/download/{owner}/{name}/1.9.4/",

                    Downloads = 500000

                },

                new ThunderstorePackageVersion

                {

                    Name = name,

                    FullName = $"{owner}-{name}-1.9.3",

                    VersionNumber = "1.9.3",

                    Description = "Older version.",

                    DownloadUrl = $"https://thunderstore.io/package/download/{owner}/{name}/1.9.3/",

                    Downloads = 200000

                }

            ]

        };

    }



    [Fact]

    public async Task Open_LoadsPackagesAndCategories_ForCommunity()

    {

        // Arrange

        var pkg = CreateSamplePackage();

        _thunderstoreService.Setup(s => s.SearchPackagesAsync(

                "lethal-company", "", "All", "Relevance", false,

                It.IsAny<CancellationToken>(),

                It.IsAny<IEnumerable<string>?>(),

                It.IsAny<IEnumerable<string>?>()))

            .ReturnsAsync([pkg]);



        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        var instance = new GameInstance

        {

            Id = Guid.NewGuid(),

            Name = "Lethal Company",

            InstallPath = "C:\\Games\\Lethal Company"

        };



        // Act

        vm.Open(instance, "lethal-company");

        await vm.LoadPackagesCommand.ExecuteAsync(null);



        // Assert

        vm.IsOpen.Should().BeTrue();

        vm.Community.Should().Be("lethal-company");

        vm.InstanceName.Should().Be("Lethal Company");

        vm.Packages.Should().ContainSingle();

        vm.Packages[0].Name.Should().Be("MoreCompany");

        vm.Packages[0].FormattedDownloads.Should().Be("1.3M");

        vm.AvailableCategories.Should().Contain(["All", "BepInEx", "Client", "Server"]);

    }



    [Fact]

    public async Task ToggleSortDirection_TogglesDescendingStateAndReloads()

    {

        // Arrange

        var pkg = CreateSamplePackage();

        _thunderstoreService.Setup(s => s.SearchPackagesAsync(

                "lethal-company", "", "All", "Relevance", true,

                It.IsAny<CancellationToken>(),

                It.IsAny<IEnumerable<string>?>(),

                It.IsAny<IEnumerable<string>?>()))

            .ReturnsAsync([pkg]);



        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Lethal Company", InstallPath = "C:\\Games" };

        vm.Open(instance, "lethal-company");



        // Act

        vm.IsSortDescending.Should().BeTrue();

        vm.ToggleSortDirection();



        // Assert

        vm.IsSortDescending.Should().BeFalse();

        await vm.LoadPackagesCommand.ExecuteAsync(null);

        vm.Packages.Should().ContainSingle();

    }



    [Fact]

    public async Task InstallItemAsync_DownloadsAndInstallsPackage_UpdatesInstalledState()

    {

        // Arrange

        var pkg = CreateSamplePackage();

        var downloadedModPackage = new ModPackage

        {

            Path = "C:\\Temp\\MoreCompany.zip",

            Name = "MoreCompany",

            Source = new ModSourceRef

            {

                Provider = "thunderstore",

                RemoteId = "lethal-company:notnotnotswipez:MoreCompany",

                Version = "1.9.4"

            },

            Layout = ModPackageLayout.BepInExPackage

        };



        _thunderstoreService.Setup(s => s.DownloadPackageAsync(

                "lethal-company",

                pkg,

                It.IsAny<ThunderstorePackageVersion>(),

                It.IsAny<IProgress<double>>(),

                It.IsAny<CancellationToken>()))

            .ReturnsAsync(downloadedModPackage);



        var refreshed = false;

        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            package =>

            {

                _installedModPackage = package;

                return Task.FromResult<ModInstallResult?>(new ModInstallResult

                {

                    Status = ModInstallStatus.Installed,

                    Record = new InstalledModRecord

                    {

                        Id = "morecompany",

                        Name = "MoreCompany",

                        Strategy = "BepInExPackage",

                        Source = package.Source!,

                        Files = []

                    }

                });

            },

            null,

            NullLogger.Instance)

        {

            OnModInstalledCallback = () => { refreshed = true; return Task.CompletedTask; }

        };



        var instance = new GameInstance

        {

            Id = Guid.NewGuid(),

            Name = "Lethal Company",

            InstallPath = "C:\\Games\\Lethal Company"

        };

        vm.Open(instance, "lethal-company");



        var item = new ThunderstorePackageItem(pkg, vm.InstallItemAsync);



        // Act

        await item.InstallCommand.ExecuteAsync(null);



        // Assert

        _installedModPackage.Should().NotBeNull();

        _installedModPackage!.Source?.Provider.Should().Be("thunderstore");

        item.IsInstalled.Should().BeTrue();

        item.InstalledVersion.Should().Be("1.9.4");

        item.ButtonLabel.Should().Be("Installed");

        refreshed.Should().BeTrue();

    }



    [Fact]

    public void SwitchToWorkshop_ClosesThunderstoreAndInvokesAction()

    {

        // Arrange

        var switched = false;

        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Lethal Company", InstallPath = "C:\\Games" };

        vm.Open(instance, "lethal-company", hasWorkshop: true, switchToWorkshop: () => switched = true);



        // Act

        vm.SwitchToWorkshopCommand.Execute(null);



        // Assert

        vm.IsOpen.Should().BeFalse();

        switched.Should().BeTrue();

    }



    [Fact]

    public void ToggleTag_CyclesThroughNeutralIncludeExclude()

    {

        // Arrange

        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        var tag = new ThunderstoreCategoryTag { Name = "BepInEx", DisplayName = "BepInEx", State = TagFilterState.Neutral };

        vm.AvailableTags.Add(tag);



        // Act 1: 1st click -> Include

        vm.ToggleTagCommand.Execute(tag);

        tag.State.Should().Be(TagFilterState.Include);

        vm.HasActiveCategoryFilter.Should().BeTrue();



        // Act 2: 2nd click -> Exclude

        vm.ToggleTagCommand.Execute(tag);

        tag.State.Should().Be(TagFilterState.Exclude);

        vm.HasActiveCategoryFilter.Should().BeTrue();



        // Act 3: 3rd click -> Neutral

        vm.ToggleTagCommand.Execute(tag);

        tag.State.Should().Be(TagFilterState.Neutral);

        vm.HasActiveCategoryFilter.Should().BeFalse();

    }



    [Fact]

    public void ExcludeTag_TogglesBetweenNeutralAndExclude()

    {

        // Arrange

        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        var tag = new ThunderstoreCategoryTag { Name = "Server", DisplayName = "Server", State = TagFilterState.Neutral };

        vm.AvailableTags.Add(tag);



        // Act 1: Right-click on neutral -> Exclude

        vm.ExcludeTagCommand.Execute(tag);

        tag.State.Should().Be(TagFilterState.Exclude);

        vm.HasActiveCategoryFilter.Should().BeTrue();



        // Act 2: Right-click again -> Neutral

        vm.ExcludeTagCommand.Execute(tag);

        tag.State.Should().Be(TagFilterState.Neutral);

        vm.HasActiveCategoryFilter.Should().BeFalse();

    }



    [Fact]

    public void SelectCategoryTag_FindsTagAndCyclesState()

    {

        // Arrange

        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        var tag = new ThunderstoreCategoryTag { Name = "BepInEx", DisplayName = "BepInEx", State = TagFilterState.Neutral };

        vm.AvailableTags.Add(tag);



        // Act 1: Select "BepInEx" -> cycles Neutral to Include

        vm.SelectCategoryTagCommand.Execute("BepInEx");

        tag.State.Should().Be(TagFilterState.Include);



        // Act 2: Select "BepInEx" again -> cycles to Exclude

        vm.SelectCategoryTagCommand.Execute("BepInEx");

        tag.State.Should().Be(TagFilterState.Exclude);



        // Act 3: Select "BepInEx" again -> cycles to Neutral

        vm.SelectCategoryTagCommand.Execute("BepInEx");

        tag.State.Should().Be(TagFilterState.Neutral);

    }



    [Fact]

    public void OpenPackageDetail_InvokesCallback_WithPackageNameAndUrl()

    {

        // Arrange

        string? openedTitle = null;

        string? openedUrl = null;



        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance)

        {

            OpenModPageAction = (title, url) =>

            {

                openedTitle = title;

                openedUrl = url;

            }

        };



        var pkg = CreateSamplePackage("BackpackMod", "author");

        var item = new ThunderstorePackageItem(pkg, vm.InstallItemAsync);



        // Act

        vm.OpenPackageDetailCommand.Execute(item);



        // Assert

        openedTitle.Should().Be("BackpackMod");

        openedUrl.Should().Be("https://thunderstore.io/package/author/BackpackMod/");

    }



    [Fact]

    public void Sort_DefaultsToNull_AndAppliesSortCriteriaWhenSelected()

    {

        // Arrange

        var vm = new ThunderstoreBrowserViewModel(

            _thunderstoreService.Object,

            _ => Task.FromResult<ModInstallResult?>(null),

            null,

            NullLogger.Instance);



        // Assert default is empty / "Ordenar"

        vm.SelectedSort.Should().BeNull();

        vm.SelectedSortDisplayName.Should().Be("Ordenar");

        vm.HasActiveSort.Should().BeFalse();



        // Act: choose sort

        vm.SelectedSort = "Descargas";



        // Assert

        vm.SelectedSortDisplayName.Should().Be("Descargas");

        vm.HasActiveSort.Should().BeTrue();



        // Reset

        vm.ResetSortCommand.Execute(null);

        vm.SelectedSort.Should().BeNull();

        vm.SelectedSortDisplayName.Should().Be("Ordenar");

        vm.HasActiveSort.Should().BeFalse();

    }



    [Fact]
    public async Task VirtualWindowing_LoadsVisiblePlusThreeBufferRows_AndUnloadsPreviousOnScroll()
    {
        // Arrange: 50 packages
        var packages = Enumerable.Range(0, 50).Select(i => CreateSamplePackage($"Mod_{i:D2}", "Author")).ToList();

        _thunderstoreService.Setup(s => s.SearchPackagesAsync(
                "lethal-company", "", "All", "Relevance", false,
                It.IsAny<CancellationToken>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(packages);

        var vm = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            _ => Task.FromResult<ModInstallResult?>(null),
            null,
            NullLogger.Instance);

        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Lethal Company", InstallPath = "C:\\Games" };
        vm.Open(instance, "lethal-company");
        await vm.LoadPackagesCommand.ExecuteAsync(null);

        // Assert 1: At offset 0 with viewport 650px:
        // visibleCount = ceil(650 / 112) + 1 = 6 + 1 = 7.
        // startIndex = Math.Max(0, 0 - 3) = 0.
        // endIndex = Math.Min(49, 0 + 7 + 3) = 10.
        // TopSpacerHeight = 0, BottomSpacerHeight = (50 - 1 - 10) * 112 = 39 * 112 = 4368.
        vm.Packages.Count.Should().Be(11);
        vm.Packages.First().Name.Should().Be("Mod_00");
        vm.Packages.Last().Name.Should().Be("Mod_10");
        vm.TopSpacerHeight.Should().Be(0);
        vm.BottomSpacerHeight.Should().Be(39 * ThunderstoreBrowserViewModel.PackageRowHeight);

        // Act 2: Scroll down to offset 2240px (firstVisibleIndex = 2240 / 112 = 20):
        // startIndex = 20 - 3 = 17.
        // endIndex = 20 + 7 + 3 = 30.
        vm.UpdateScrollWindow(2240, 650);

        // Assert 2: Previous items (0..16) are unloaded from memory!
        vm.Packages.First().Name.Should().Be("Mod_17");
        vm.Packages.Last().Name.Should().Be("Mod_30");
        vm.Packages.Any(p => p.Name == "Mod_00").Should().BeFalse(); // unloaded from memory
        vm.TopSpacerHeight.Should().Be(17 * ThunderstoreBrowserViewModel.PackageRowHeight);
        vm.BottomSpacerHeight.Should().Be((49 - 30) * ThunderstoreBrowserViewModel.PackageRowHeight);

        // Act 3: Scroll back up to offset 0:
        vm.UpdateScrollWindow(0, 650);

        // Assert 3: Items 0..10 are reloaded instantly from _allFilteredPackages without internet
        vm.Packages.First().Name.Should().Be("Mod_00");
        vm.Packages.Last().Name.Should().Be("Mod_10");
        vm.Packages.Any(p => p.Name == "Mod_30").Should().BeFalse(); // item 30 unloaded
        vm.TopSpacerHeight.Should().Be(0);
        vm.BottomSpacerHeight.Should().Be(39 * ThunderstoreBrowserViewModel.PackageRowHeight);
    }

    [Fact]
    public async Task ProgressiveScroll_UnrendersPreviousItems_AndNeverSaturatesMemory()
    {
        // Arrange: 100 packages
        var packages = Enumerable.Range(0, 100).Select(i => CreateSamplePackage($"Mod_{i:D3}", "Author")).ToList();

        _thunderstoreService.Setup(s => s.SearchPackagesAsync(
                "lethal-company", "", "All", "Relevance", false,
                It.IsAny<CancellationToken>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(packages);

        var vm = new ThunderstoreBrowserViewModel(
            _thunderstoreService.Object,
            _ => Task.FromResult<ModInstallResult?>(null),
            null,
            NullLogger.Instance);

        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Lethal Company", InstallPath = "C:\\Games" };
        vm.Open(instance, "lethal-company");
        await vm.LoadPackagesCommand.ExecuteAsync(null);

        // Act & Assert across continuous scrolling steps
        // As scroll advances down to row 50, previously seen items MUST be unrendered (removed from Packages)
        for (int row = 0; row <= 50; row += 2)
        {
            double offset = row * ThunderstoreBrowserViewModel.PackageRowHeight;
            vm.UpdateScrollWindow(offset, 650.0);

            // Bounded window size: Packages count should never exceed visibleCount + 2 * BufferRows (~13 items)
            // It MUST NOT accumulate infinitely like an endless list
            vm.Packages.Count.Should().BeLessThanOrEqualTo(14);

            // If we have scrolled past row 10, items before (row - BufferRows) MUST be unrendered
            int cutoff = row - ThunderstoreBrowserViewModel.BufferRows;
            if (cutoff > 0)
            {
                for (int oldIndex = 0; oldIndex < cutoff; oldIndex++)
                {
                    string oldModName = $"Mod_{oldIndex:D3}";
                    vm.Packages.Any(p => p.Name == oldModName)
                        .Should().BeFalse($"Item {oldModName} should have been unrendered/unloaded from UI memory at row {row}");
                }
            }

            // Total mathematical height (TopSpacer + Rendered Items + BottomSpacer) remains constant at 100 * 112px
            double totalVirtualHeight = vm.TopSpacerHeight + (vm.Packages.Count * ThunderstoreBrowserViewModel.PackageRowHeight) + vm.BottomSpacerHeight;
            totalVirtualHeight.Should().Be(100 * ThunderstoreBrowserViewModel.PackageRowHeight);
        }
    }
}
