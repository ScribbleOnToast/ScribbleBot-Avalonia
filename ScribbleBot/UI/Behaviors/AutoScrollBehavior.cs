using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Utils;

namespace ScribbleBot.UI.Behaviors;

public static class AutoScrollBehavior
{
    public static readonly AttachedProperty<bool> AutoScrollProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>(
            "AutoScroll",
            typeof(AutoScrollBehavior),
            false);

    static AutoScrollBehavior()
    {
        AutoScrollProperty.Changed.Subscribe(OnAutoScrollChanged);
    }

    public static bool GetAutoScroll(AvaloniaObject obj) => obj.GetValue(AutoScrollProperty);
    public static void SetAutoScroll(AvaloniaObject obj, bool value) => obj.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(AvaloniaPropertyChangedEventArgs<bool> e)
    {
        if (e.Sender is ScrollViewer scrollViewer)
        {
            if (e.NewValue.Value)
            {
                scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
            }
            else
            {
                scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
            }
        }
    }

    private static void ScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            // If the extent height expanded, scroll to bottom automatically
            if (e.ExtentDelta.Y > 0)
            {
                scrollViewer.ScrollToEnd();
            }
        }
    }
}