using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using BlueStar.App.Controls;
using BlueStar.App.Converters;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace BlueStar.App.Tests;

public class FlyoutOverlayAndConvertersTests
{
    private static void RunOnStaThread(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (ex != null)
        {
            throw new AggregateException(ex);
        }
    }

    [Fact]
    public void FlyoutOverlay_Defaults_AreCorrect()
    {
        RunOnStaThread(() =>
        {
            var overlay = new FlyoutOverlay();
            Assert.False(overlay.IsOpen);
            Assert.True(overlay.CloseOnBackdropClick);
            Assert.Equal(8.0, overlay.SlideOffset);
            Assert.Null(overlay.CloseCommand);
        });
    }

    [Fact]
    public void FlyoutOverlay_IsOpen_TransitionsVisibilityAndHitTesting()
    {
        RunOnStaThread(() =>
        {
            var overlay = new FlyoutOverlay();
            Assert.False(overlay.IsOpen);

            overlay.IsOpen = true;
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(overlay.IsHitTestVisible);

            overlay.IsOpen = false;
            Assert.False(overlay.IsHitTestVisible);
        });
    }

    [Fact]
    public void FlyoutOverlay_CloseCommand_ExecutesWhenRequested()
    {
        RunOnStaThread(() =>
        {
            bool executed = false;
            var overlay = new FlyoutOverlay
            {
                IsOpen = true,
                CloseCommand = new RelayCommand(() => executed = true)
            };

            overlay.RequestClose();
            Assert.True(executed);
        });
    }

    [Fact]
    public void FlyoutOverlay_SlideOffset_CanBeConfigured()
    {
        RunOnStaThread(() =>
        {
            var overlay = new FlyoutOverlay { SlideOffset = 16.0 };
            Assert.Equal(16.0, overlay.SlideOffset);
        });
    }

    [Fact]
    public void EqualsToBoolConverter_MatchingValues_ReturnsTrue()
    {
        var converter = EqualsToBoolConverter.Instance;
        var guid = Guid.NewGuid();

        var result = converter.Convert(new object[] { guid, guid }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.True(Assert.IsType<bool>(result));

        var stringResult = converter.Convert(new object[] { "Explore", "Explore" }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.True(Assert.IsType<bool>(stringResult));
    }

    [Fact]
    public void EqualsToBoolConverter_NonMatchingValues_ReturnsFalse()
    {
        var converter = EqualsToBoolConverter.Instance;
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();

        var result = converter.Convert(new object[] { guid1, guid2 }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False(Assert.IsType<bool>(result));

        var stringResult = converter.Convert(new object[] { "Explore", "Home" }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False(Assert.IsType<bool>(stringResult));
    }

    [Fact]
    public void EqualsToBoolConverter_NullOrUnsetValue_ReturnsFalse()
    {
        var converter = EqualsToBoolConverter.Instance;
        var guid = Guid.NewGuid();

        var nullResult = converter.Convert(new object?[] { guid, null }!, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False(Assert.IsType<bool>(nullResult));

        var unsetResult = converter.Convert(new object[] { guid, DependencyProperty.UnsetValue }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False(Assert.IsType<bool>(unsetResult));

        var emptyResult = converter.Convert(Array.Empty<object>(), typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False(Assert.IsType<bool>(emptyResult));

        var singleResult = converter.Convert(new object[] { guid }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False(Assert.IsType<bool>(singleResult));
    }

    [Fact]
    public void EqualsToBoolConverter_ConvertBack_ThrowsNotSupportedException()
    {
        var converter = EqualsToBoolConverter.Instance;
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(true, new[] { typeof(object), typeof(object) }, null!, CultureInfo.InvariantCulture));
    }
}
