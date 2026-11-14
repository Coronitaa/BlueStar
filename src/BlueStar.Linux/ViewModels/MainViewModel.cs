namespace BlueStar.Linux.ViewModels;

public sealed class MainViewModel(LibraryViewModel library, SettingsViewModel settings, RequirementsViewModel requirements)
{
    public LibraryViewModel Library { get; } = library;
    public SettingsViewModel Settings { get; } = settings;
    public RequirementsViewModel Requirements { get; } = requirements;
    public string Title => $"BlueStar {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} (Linux beta)";
}
