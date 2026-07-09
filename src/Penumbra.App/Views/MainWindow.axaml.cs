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

    public MainWindow()
    {
        InitializeComponent();

        // 4.5b/4.5d: the canvas reports gestures; the view-model owns what they mean.
        InkCanvas.DrawingStarted += (_, _) => ViewModel?.NotifyStrokeStarted();
        InkCanvas.AnswerTapped += (_, _) => ViewModel?.ToggleAnswerProvenance();

        // The view-model holds a live-recognition timer; closing the window must stop it.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ViewModel is not { } vm)
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

        string json = PenumbraDocumentSerializer.Serialize(vm.Document.ToDocument());
        await using Stream stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(json);
    }

    private async Task OpenAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

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

        await using Stream stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        string json = await reader.ReadToEndAsync();
        vm.Document.Load(PenumbraDocumentSerializer.Deserialize(json));
    }
}
