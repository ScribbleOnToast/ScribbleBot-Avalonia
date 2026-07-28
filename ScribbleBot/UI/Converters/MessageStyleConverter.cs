using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.AI;
using System.Globalization;

namespace ScribbleBot.UI.Converters;

public class MessageStyleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ChatRole role)
        {
            bool isUser = role == ChatRole.User;
            string param = parameter?.ToString() ?? string.Empty;

            return param switch
            {
                "Alignment" => isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                "Background" => new SolidColorBrush(Color.Parse(isUser ? "#2A2D3D" : "#252526")),
                "BorderBrush" => new SolidColorBrush(Color.Parse(isUser ? "#007ACC" : "#10B981")),
                "RoleText" => isUser ? "YOU" : "SCRIBBLEBOT",
                "RoleColor" => new SolidColorBrush(Color.Parse(isUser ? "#007ACC" : "#10B981")),
                _ => AvaloniaProperty.UnsetValue
            };
        }

        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}