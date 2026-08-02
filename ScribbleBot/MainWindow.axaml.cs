using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using ScribbleBot.UI.ViewModels;
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
        //user dropped a file
        if (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnTextBoxDrop(object? sender, DragEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            if (vm != null && e != null) await vm.AttachFiles(e.DataTransfer.TryGetFiles());
        }
        else if(e.DataTransfer.Contains(DataFormat.Text))
        {
            if (vm != null && e != null) vm.UserInput += e.DataTransfer.TryGetText();
        }
    }
}