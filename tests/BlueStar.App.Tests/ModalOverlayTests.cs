using System;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using BlueStar.App.Controls;
using Xunit;

namespace BlueStar.App.Tests;

public class ModalOverlayTests
{
    private void RunOnStaThread(Action action)
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
            var overlay = new ModalOverlay();
            Assert.False(overlay.IsOpen);
            Assert.True(overlay.CloseOnEscape);
            Assert.False(overlay.CloseOnBackdropClick);
            Assert.Null(overlay.CloseCommand);
            Assert.NotNull(overlay.BackdropBrush);
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

    private class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
