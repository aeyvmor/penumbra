using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Penumbra.App.ViewModels;
using Penumbra.Core;

namespace Penumbra.App.Views;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType PenFileType =
        new("Penumbra page") { Patterns = new[] { "*.pen" } };
    private bool _startupRecoveryInProgress;
    private bool _fileOperationInProgress;
    private bool _closeRequestedWhileBusy;
    private bool _closeFlushInProgress;
    private bool _closeAuthorized;

    public MainWindow()
    {
        InitializeComponent();

        // 4.5b/4.5d/5.3: the canvas reports gestures; the view-model owns what they mean.
        InkCanvas.DrawingStarted += (_, _) => ViewModel?.NotifyStrokeStarted();
        InkCanvas.AnswerTapped += (_, e) => ViewModel?.ToggleAnswerProvenance(e.OwnerId);
        InkCanvas.AnswerDragCompleted += (_, e) =>
            ViewModel?.StampAnswer(e.OwnerId, e.WorldDx, e.WorldDy, e.WorldDropX, e.WorldDropY);
        InkCanvas.AnswerDragCancelled += (_, _) => ViewModel?.NotifyAnswerDragCancelled();
        InkCanvas.TaffyStarted += (_, e) => e.Accepted = ViewModel?.BeginTaffy(e.OwnerId, e.Run) == true;
        InkCanvas.TaffyMoved += (_, e) => ViewModel?.UpdateTaffy(e.ScreenDx);
        InkCanvas.TaffyEnded += (_, _) => ViewModel?.EndTaffy();

        Opened += OnOpened;
        Closing += OnClosing;

        // The view-model holds local timers/coordinators; a successfully flushed close disposes them.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_startupRecoveryInProgress
            || _fileOperationInProgress
            || _closeFlushInProgress
            || ViewModel is not { } vm)
        {
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl:
                vm.Document.Undo();
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                vm.Document.Redo();
                e.Handled = true;
                break;
            case Key.Delete:
                vm.Document.Clear();
                e.Handled = true;
                break;
            case Key.S when ctrl:
                _ = SaveAsync();
                e.Handled = true;
                break;
            case Key.O when ctrl:
                _ = OpenAsync();
                e.Handled = true;
                break;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();

    private async Task SaveAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (!TryBeginFileOperation(vm))
        {
            return;
        }

        try
        {
            string? path = vm.CurrentPath;
            if (path is null)
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Penumbra page",
                    DefaultExtension = "pen",
                    SuggestedFileName = "page.pen",
                    FileTypeChoices = new[] { PenFileType },
                });

                if (file is null)
                {
                    return;
                }

                path = file.TryGetLocalPath();
                if (path is null)
                {
                    vm.ReportPersistenceFailure(
                        "This storage provider has no crash-safe local path; choose a local folder.");
                    return;
                }
            }

            await vm.SavePageAsync(path);
        }
        catch (OperationCanceledException)
        {
            // The view-model already published the honest cancellation state.
        }
        catch (Exception)
        {
            // The view-model already published the non-destructive failure state.
        }
        finally
        {
            EndFileOperation();
        }
    }

    private async Task OpenAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (!TryBeginFileOperation(vm))
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Penumbra page",
                AllowMultiple = false,
                FileTypeFilter = new[] { PenFileType },
            });

            if (files.Count == 0)
            {
                return;
            }

            string? path = files[0].TryGetLocalPath();
            if (path is null)
            {
                vm.ReportPersistenceFailure(
                    "This storage provider has no validated local path; choose a local file.");
                return;
            }

            await vm.OpenPageAsync(path);
        }
        catch (OperationCanceledException)
        {
            // The view-model already published the honest cancellation state.
        }
        catch (Exception)
        {
            // The view-model already published the non-destructive failure state.
        }
        finally
        {
            EndFileOperation();
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        _startupRecoveryInProgress = true;
        Workspace.IsEnabled = false;
        try
        {
            await vm.RecoverInterruptedSessionAsync();
        }
        catch (Exception)
        {
            // Recovery state is visible in the footer; a blank/current canvas remains usable.
        }
        finally
        {
            _startupRecoveryInProgress = false;
            if (_closeRequestedWhileBusy)
            {
                Close();
            }
            else
            {
                Workspace.IsEnabled = true;
            }
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAuthorized || ViewModel is not { } vm)
        {
            return;
        }

        e.Cancel = true;
        if (_startupRecoveryInProgress || _fileOperationInProgress)
        {
            _closeRequestedWhileBusy = true;
            Workspace.IsEnabled = false;
            return;
        }

        if (_closeFlushInProgress)
        {
            return;
        }

        _closeRequestedWhileBusy = false;
        _closeFlushInProgress = true;
        Workspace.IsEnabled = false;
        try
        {
            await vm.CompleteCleanShutdownAsync();
            _closeAuthorized = true;
            Close();
        }
        catch (Exception)
        {
            // Keep the window open and the recovery checkpoint intact. A later close retries the flush.
            _closeFlushInProgress = false;
            Workspace.IsEnabled = true;
        }
    }

    private bool TryBeginFileOperation(MainWindowViewModel vm)
    {
        if (_startupRecoveryInProgress || _fileOperationInProgress || _closeFlushInProgress)
        {
            vm.ReportPersistenceFailure("Another page or shutdown operation is still running.");
            return false;
        }

        _fileOperationInProgress = true;
        Workspace.IsEnabled = false;
        return true;
    }

    private void EndFileOperation()
    {
        _fileOperationInProgress = false;
        if (_closeRequestedWhileBusy)
        {
            Close();
        }
        else if (!_startupRecoveryInProgress && !_closeFlushInProgress)
        {
            Workspace.IsEnabled = true;
        }
    }
}
