using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace ScribbleBot.UI
{
    public static class ThemeManager
    {
        public static void ApplyTheme(bool isDarkMode)
        {
            string themeUri = isDarkMode
                ? "avares://ScribbleBot/UI/Themes/DarkTheme.axaml"
                : "avares://ScribbleBot/UI/Themes/LightTheme.axaml";

            var newTheme = new ResourceInclude(new Uri("avares://ScribbleBot/App.axaml"))
            {
                Source = new Uri(themeUri)
            };

            // Replace the merged theme dictionary in App.Resources
            if (Application.Current is App app)
            {
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(newTheme);
            }
        }
    }
}
