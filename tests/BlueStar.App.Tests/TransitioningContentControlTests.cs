using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BlueStar.App.Controls;
using Xunit;

namespace BlueStar.App.Tests;

public class TransitioningContentControlTests
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
    public void Defaults_AreCorrect()
    {
        RunOnStaThread(() =>
        {
            var control = new TransitioningContentControl();
            Assert.Equal(TimeSpan.FromMilliseconds(200), control.TransitionDuration);
            Assert.Equal(8.0, control.TransitionOffset);
            Assert.False(control.IsTabStop);
        });
    }

    [Fact]
    public void ApplyTemplate_WithRealTemplate_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            var template = new ControlTemplate(typeof(TransitioningContentControl));
            var rootFactory = new FrameworkElementFactory(typeof(Grid));
            var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter), TransitioningContentControl.PartContentPresenter);
            rootFactory.AppendChild(presenterFactory);
            template.VisualTree = rootFactory;

            var control = new TransitioningContentControl
            {
                Template = template,
                Content = new TextBlock { Text = "Initial View" }
            };

            control.ApplyTemplate();

            // Changing content must not throw
            control.Content = new TextBlock { Text = "Second View" };
        });
    }

    [Fact]
    public void ContentChange_RapidSuccession_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            var template = new ControlTemplate(typeof(TransitioningContentControl));
            var rootFactory = new FrameworkElementFactory(typeof(Grid));
            var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter), TransitioningContentControl.PartContentPresenter);
            rootFactory.AppendChild(presenterFactory);
            template.VisualTree = rootFactory;

            var control = new TransitioningContentControl
            {
                Template = template
            };

            control.ApplyTemplate();

            for (int i = 0; i < 20; i++)
            {
                control.Content = new TextBlock { Text = $"View {i}" };
            }
        });
    }
}
