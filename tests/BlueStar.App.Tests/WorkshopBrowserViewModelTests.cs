using System;
using System.Threading.Tasks;
using BlueStar.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BlueStar.App.Tests;

public sealed class WorkshopBrowserViewModelTests
{
    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3100200300", 3100200300UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?searchtext=&id=42424242&l=spanish", 42424242UL)]
    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=987654", 987654UL)]
    [InlineData("  3100200300 ", 3100200300UL)]
    public void ParseItemId_ReadsItemPagesAndBareIds(string input, ulong expected)
    {
        WorkshopBrowserViewModel.ParseItemId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/app/4704690/workshop/")]
    [InlineData("https://steamcommunity.com/workshop/browse/?appid=4704690&id=55555")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000000/?id=1234")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseItemId_IgnoresPagesThatAreNotItems(string? input)
    {
        WorkshopBrowserViewModel.ParseItemId(input).Should().BeNull();
    }

    [Fact]
    public async Task Navigating_ToAnItem_EnablesDownload_WhichInstallsThatItem()
    {
        // Arrange
        ulong? installed = null;
        var vm = new WorkshopBrowserViewModel((id, _) => { installed = id; return Task.FromResult(true); });
        vm.Open(4704690);

        // Assert: the Workshop front page is not an item
        vm.StartUrl.Should().Be("https://steamcommunity.com/app/4704690/workshop/");
        vm.DownloadCommand.CanExecute(null).Should().BeFalse();

        // Act
        vm.OnNavigated("https://steamcommunity.com/sharedfiles/filedetails/?id=3100200300");
        await vm.DownloadCommand.ExecuteAsync(null);

        // Assert
        installed.Should().Be(3100200300UL);
        vm.IsDownloading.Should().BeFalse();
    }

    [Fact]
    public void ManualInput_SelectsAnItem_WhenTheBrowserIsNotOnOne()
    {
        // Arrange
        var vm = new WorkshopBrowserViewModel((_, _) => Task.FromResult(true));
        vm.Open(4704690);

        // Act
        vm.ManualInput = "https://steamcommunity.com/sharedfiles/filedetails/?id=777888";

        // Assert
        vm.CurrentItemId.Should().Be(777888UL);
        vm.DownloadCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Close_IsIgnoredWhileDownloading()
    {
        // Arrange
        var gate = new TaskCompletionSource<bool>();
        var vm = new WorkshopBrowserViewModel((_, _) => gate.Task);
        vm.Open(4704690);
        vm.OnNavigated("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567");
        _ = vm.DownloadCommand.ExecuteAsync(null);

        // Act
        vm.CloseCommand.Execute(null);

        // Assert
        vm.IsOpen.Should().BeTrue();
        gate.SetResult(true);
    }
}
