using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// State of the Steam Workshop browser modal: the page the embedded browser shows, which Workshop item
/// (if any) is open in it, and the download of that item. The view feeds navigations in through
/// <see cref="OnNavigated"/>; the actual install is delegated to the Mods tab.
/// </summary>
public sealed partial class WorkshopBrowserViewModel : ObservableObject
{
    private static readonly Regex ItemIdInUrl = new(@"[?&]id=(\d{3,20})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BareItemId = new(@"^\s*(\d{3,20})\s*$", RegexOptions.Compiled);

    private readonly Func<ulong, IProgress<double>, Task<bool>> _installItem;

    public WorkshopBrowserViewModel(Func<ulong, IProgress<double>, Task<bool>> installItem)
    {
        _installItem = installItem ?? throw new ArgumentNullException(nameof(installItem));
    }

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Address the browser should load. Changing it makes the view navigate.</summary>
    [ObservableProperty]
    private string? _startUrl;

    [ObservableProperty]
    private string? _currentUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private ulong? _currentItemId;

    /// <summary>Link or id typed by hand, for when the item is not open in the browser (or WebView2 is missing).</summary>
    [ObservableProperty]
    private string _manualInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    public bool HasItem => CurrentItemId.HasValue;

    public bool CanDownload => CurrentItemId.HasValue && !IsDownloading;

    /// <summary>Opens the modal on the game's Workshop front page.</summary>
    public void Open(uint appId)
    {
        StatusMessage = null;
        IsStatusError = false;
        Progress = 0;
        ManualInput = string.Empty;
        CurrentItemId = null;
        StartUrl = appId > 0
            ? $"https://steamcommunity.com/app/{appId}/workshop/"
            : "https://steamcommunity.com/workshop/";
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        if (IsDownloading) return;
        IsOpen = false;
    }

    /// <summary>Called by the view whenever the embedded browser lands on a new address.</summary>
    public void OnNavigated(string? url)
    {
        CurrentUrl = url;
        CurrentItemId = ParseItemId(url);
        if (CurrentItemId.HasValue)
        {
            ManualInput = string.Empty;
        }
    }

    partial void OnManualInputChanged(string value)
    {
        var parsed = ParseItemId(value);
        if (parsed.HasValue) CurrentItemId = parsed;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    public async Task DownloadAsync()
    {
        if (CurrentItemId is not { } itemId) return;

        IsDownloading = true;
        IsStatusError = false;
        Progress = 0;
        StatusMessage = null;
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            await _installItem(itemId, progress).ConfigureAwait(true);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Shows the outcome of an install in the modal.</summary>
    public void ReportStatus(string? message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    /// <summary>Extracts a PublishedFileId from a Workshop item URL ("...filedetails/?id=123") or a bare id.</summary>
    public static ulong? ParseItemId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Only item pages count: browse and profile pages also carry ?id= style parameters.
        var match = input.Contains("filedetails", StringComparison.OrdinalIgnoreCase)
            ? ItemIdInUrl.Match(input)
            : BareItemId.Match(input);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : null;
    }
}
