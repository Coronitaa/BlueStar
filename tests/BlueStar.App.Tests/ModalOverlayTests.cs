using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueStar.App.Controls;
using Xunit;

namespace BlueStar.App.Tests;

public class ModalOverlayTests
{
    private static void RunOnStaThread(Action action) => StaTestHelper.Run(action);

    [Fact]
    public void Defaults_AreCorrect()
    {
        RunOnStaThread(() =>
        {
            var overlay = new ModalOverlay();
            Assert.False(overlay.IsOpen);
            Assert.True(overlay.CloseOnEscape);
            Assert.False(overlay.CloseOnBackdropClick);
            Assert.True(overlay.AnimateScale);
            Assert.Null(overlay.CloseCommand);
            Assert.NotNull(overlay.BackdropBrush);
        });
    }

    [Fact]
    public void AnimateScale_CanBeToggled()
    {
        RunOnStaThread(() =>
        {
            var overlay = new ModalOverlay { AnimateScale = false };
            Assert.False(overlay.AnimateScale);
        });
    }

    [Fact]
    public void RequestClose_ExecutesCloseCommand_WhenProvided()
    {
        RunOnStaThread(() =>
        {
            var overlay = new ModalOverlay();
            bool executed = false;
            overlay.CloseCommand = new RelayCommand(_ => executed = true);
            overlay.IsOpen = true;

            overlay.RequestClose();

            Assert.True(executed);
        });
    }

    [Fact]
    public void RequestClose_SetsIsOpenFalse_WhenNoCommandProvided()
    {
        RunOnStaThread(() =>
        {
            var overlay = new ModalOverlay();
            overlay.IsOpen = true;

            overlay.RequestClose();

            Assert.False(overlay.IsOpen);
        });
    }

    [Fact]
    public void BackdropBrush_CanBeCustomized()
    {
        RunOnStaThread(() =>
        {
            var overlay = new ModalOverlay();
            var customBrush = new SolidColorBrush(Colors.Red);
            overlay.BackdropBrush = customBrush;

            Assert.Same(customBrush, overlay.BackdropBrush);
        });
    }

    [Fact]
    public void ApplyTemplate_WithRealControlTemplateStructure_DoesNotCrashOnFrozenScaleTransform()
    {
        RunOnStaThread(() =>
        {
            // Build the exact ControlTemplate structure defined in Controls.xaml
            var template = new ControlTemplate(typeof(ModalOverlay));
            var rootFactory = new FrameworkElementFactory(typeof(Grid), ModalOverlay.PartRoot);

            var backdropFactory = new FrameworkElementFactory(typeof(Border), ModalOverlay.PartBackdrop);
            rootFactory.AppendChild(backdropFactory);

            var contentHolderFactory = new FrameworkElementFactory(typeof(Grid), ModalOverlay.PartContentHolder);
            var contentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentHolderFactory.AppendChild(contentPresenterFactory);
            rootFactory.AppendChild(contentHolderFactory);

            template.VisualTree = rootFactory;

            var overlay = new ModalOverlay
            {
                Template = template,
                Content = new TextBlock { Text = "Test Content" }
            };

            // Applying template when IsOpen = false must not crash
            overlay.ApplyTemplate();
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.False(overlay.IsHitTestVisible);

            // Toggling IsOpen to true and applying template must not crash
            overlay.IsOpen = true;
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(overlay.IsHitTestVisible);

            // Toggling IsOpen back to false must not crash
            overlay.IsOpen = false;
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.False(overlay.IsHitTestVisible);
        });
    }

    [Fact]
    public void ApplyTemplate_WithAnimateScaleFalse_InitializesScaleToOne()
    {
        RunOnStaThread(() =>
        {
            var template = new ControlTemplate(typeof(ModalOverlay));
            var rootFactory = new FrameworkElementFactory(typeof(Grid), ModalOverlay.PartRoot);
            var backdropFactory = new FrameworkElementFactory(typeof(Border), ModalOverlay.PartBackdrop);
            rootFactory.AppendChild(backdropFactory);
            var contentHolderFactory = new FrameworkElementFactory(typeof(Grid), ModalOverlay.PartContentHolder);
            rootFactory.AppendChild(contentHolderFactory);
            template.VisualTree = rootFactory;

            var overlay = new ModalOverlay
            {
                Template = template,
                AnimateScale = false,
                IsOpen = false
            };

            overlay.ApplyTemplate();

            var contentHolder = overlay.Template.FindName(ModalOverlay.PartContentHolder, overlay) as Grid;
            Assert.NotNull(contentHolder);
            if (contentHolder.RenderTransform is ScaleTransform st)
            {
                Assert.False(st.IsFrozen);
                Assert.Equal(1.0, st.ScaleX);
                Assert.Equal(1.0, st.ScaleY);
            }
        });
    }

    [Fact]
    public void ScaleTransform_IsNotFrozen_AndCanBeModifiedDirectly()
    {
        RunOnStaThread(() =>
        {
            var template = CreateTestTemplate();
            var overlay = new ModalOverlay { Template = template };
            overlay.ApplyTemplate();

            var contentHolder = overlay.Template.FindName(ModalOverlay.PartContentHolder, overlay) as Grid;
            Assert.NotNull(contentHolder);
            var st = Assert.IsType<ScaleTransform>(contentHolder.RenderTransform);
            Assert.False(st.IsFrozen);

            // Mutating ScaleX and ScaleY must not throw InvalidOperationException
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;
            Assert.Equal(1.0, st.ScaleX);
            Assert.Equal(1.0, st.ScaleY);
        });
    }

    [Fact]
    public void RapidToggle_IsOpen_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            var template = CreateTestTemplate();
            var overlay = new ModalOverlay { Template = template };
            overlay.ApplyTemplate();

            for (int i = 0; i < 50; i++)
            {
                overlay.IsOpen = (i % 2 == 0);
            }
        });
    }

    [Fact]
    public void FirstOpen_MaterializesTemplateAndEnablesVisibility()
    {
        RunOnStaThread(() =>
        {
            var template = CreateTestTemplate();
            var overlay = new ModalOverlay { Template = template };

            // Modal starts closed and not templated
            Assert.False(overlay.IsOpen);

            // Open for the first time
            overlay.IsOpen = true;

            Assert.True(overlay.IsOpen);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(overlay.IsHitTestVisible);

            var contentHolder = overlay.Template.FindName(ModalOverlay.PartContentHolder, overlay) as Grid;
            Assert.NotNull(contentHolder);
            Assert.NotNull(contentHolder.RenderTransform);
            Assert.IsType<ScaleTransform>(contentHolder.RenderTransform);
        });
    }

    private static ControlTemplate CreateTestTemplate()
    {
        var template = new ControlTemplate(typeof(ModalOverlay));
        var rootFactory = new FrameworkElementFactory(typeof(Grid), ModalOverlay.PartRoot);
        var backdropFactory = new FrameworkElementFactory(typeof(Border), ModalOverlay.PartBackdrop);
        rootFactory.AppendChild(backdropFactory);
        var contentHolderFactory = new FrameworkElementFactory(typeof(Grid), ModalOverlay.PartContentHolder);
        rootFactory.AppendChild(contentHolderFactory);
        template.VisualTree = rootFactory;
        return template;
    }

    private class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
