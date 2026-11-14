using System.Collections.ObjectModel;
using BlueStar.Infrastructure.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.Linux.ViewModels;

public sealed partial class RequirementsViewModel : ObservableObject
{
    public ObservableCollection<RequirementRow> Rows { get; } = [];

    [RelayCommand]
    public void Check()
    {
        Rows.Clear();
        foreach (var r in LinuxRequirements.Check())
            Rows.Add(new RequirementRow(r));
    }
}

public sealed record RequirementRow(RequirementResult Result)
{
    public string Icon => Result.Ok ? "✓" : Result.Required ? "✗" : "!";
    public string Name => Result.Required ? Result.Name : $"{Result.Name} ({L.Optional})";
    public string Detail => Result.Detail;
}
