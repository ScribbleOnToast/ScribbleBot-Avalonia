using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using ScribbleBot.ViewModels;
using System.Linq;

namespace ScribbleBot;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Register Avalonia Drag and Drop events on the input TextBox
        var textBox = this.FindControl<TextBox>("InputTextBox");
        if (textBox != null)
        {
            textBox.AddHandler(DragDrop.DragOverEvent, OnPreviewDragOver);
            textBox.AddHandler(DragDrop.DropEvent, OnTextBoxDrop);
        }

        ChatScrollViewer.ScrollChanged += (sender, e) =>
        {
            if (e.ExtentDelta.Y > 0)
            {
                ChatScrollViewer.ScrollToEnd();
            }
        };
    }

    private void OnPreviewDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnTextBoxDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var files = e.DataTransfer.TryGetFiles();
        var vm = DataContext as MainViewModel;
        if (vm == null || files == null) return;

        foreach (var file in files)
        {
            string localPath = file.Path.LocalPath;
            // Offload parsing to ingestion service
            var attachment = await vm._fileIngestionService.ProcessFileAsync(localPath);
            vm.AttachedFiles.Add(attachment);
        }
    }
}