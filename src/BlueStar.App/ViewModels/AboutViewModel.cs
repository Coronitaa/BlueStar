using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using BlueStar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueStar.App.ViewModels;

/// <summary>
/// ViewModel for the About section displaying open-source projects and third-party credits.
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";

    [ObservableProperty]
    private ObservableCollection<ProjectCredit> _projects = [];

    public AboutViewModel()
    {
        InitializeProjects();
    }

    private void InitializeProjects()
    {
        Projects =
        [
            new ProjectCredit
            {
                Name = "BlueStar Launcher",
                Role = "Core Application",
                Description = "Universal Steam game instance manager, depot staging, manifest resolver, and multi-engine launcher.",
                Author = "Coronitaa & Contributors",
                License = "GPL-3.0",
                GitHubUrl = "https://github.com/Coronitaa/BlueStar",
                CategoryBadge = "Core App",
                IsCore = true
            },
            new ProjectCredit
            {
                Name = "ReFix Emulator & Deploy Suite",
                Role = "Multi-Engine Steam Online & LAN Multiplayer Fix",
                Description = "High-performance modular Steamworks proxy, Steam Spacewar 480 online lobbies, and Goldberg offline LAN multiplayer fix.",
                Author = "Coronitaa",
                License = "MIT",
                GitHubUrl = "https://github.com/Coronitaa/ReFix",
                CategoryBadge = "Emulation",
                IsCore = true
            },
            new ProjectCredit
            {
                Name = "DepotDownloader",
                Role = "Steam Depot & Manifest Downloader",
                Description = "Cross-platform CLI tool to download content and manifests directly from Steam depots via SteamKit2.",
                Author = "SteamRE",
                License = "GPL-2.0",
                GitHubUrl = "https://github.com/SteamRE/DepotDownloader",
                CategoryBadge = "Downloader",
                IsCore = true
            },
            new ProjectCredit
            {
                Name = "SmokeAPI",
                Role = "Universal Steamworks DLC Unlocker",
                Description = "Universal Steamworks API emulator designed for automated DLC entitlement verification and unlocking.",
                Author = "acidicoala",
                License = "GPL-3.0",
                GitHubUrl = "https://github.com/acidicoala/SmokeAPI",
                CategoryBadge = "DLC Unlocker",
                IsCore = true
            },
            new ProjectCredit
            {
                Name = "CreamInstaller",
                Role = "Automatic DLC Unlocker Installer",
                Description = "Automated DLC unlocker installer for Steam, Epic Games, and Ubisoft Connect titles.",
                Author = "pointfeev",
                License = "GPL-3.0",
                GitHubUrl = "https://github.com/pointfeev/CreamInstaller",
                CategoryBadge = "DLC Tool",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "BepInEx",
                Role = "Unity Modding Engine & Plugin Injector",
                Description = "Bepis Injector Extensible — Unified modding framework and runtime patcher for Unity (Mono & IL2CPP) and .NET games.",
                Author = "BepInEx Team",
                License = "LGPL-2.1",
                GitHubUrl = "https://github.com/BepInEx/BepInEx",
                CategoryBadge = "Modding",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "Goldberg Emulator",
                Role = "Steam LAN Emulator",
                Description = "Steam emulator that enables LAN and local multiplayer without requiring Steam client or internet connection.",
                Author = "Mr_Goldberg",
                License = "GPL-3.0",
                GitHubUrl = "https://gitlab.com/Mr_Goldberg/goldberg_emulator",
                CategoryBadge = "Emulation",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "Goldberg Emulator Fork (gbe_fork)",
                Role = "Maintained Goldberg Emulator Fork",
                Description = "Modern actively maintained continuation of the Goldberg Steam emulator with extended API support.",
                Author = "Detanup01",
                License = "GPL-3.0",
                GitHubUrl = "https://github.com/Detanup01/gbe_fork",
                CategoryBadge = "Emulation",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "SteamKit2",
                Role = ".NET Steam Network Client Library",
                Description = "Comprehensive .NET library used to interface with Valve's Steam network, CM servers, and depot protocols.",
                Author = "SteamRE",
                License = "LGPL-2.1",
                GitHubUrl = "https://github.com/SteamRE/SteamKit",
                CategoryBadge = "Networking",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "QRCoder",
                Role = "Pure C# QR Code Generator",
                Description = "High performance, zero-dependency pure C# QR code library used for mobile companion links.",
                Author = "codebude",
                License = "MIT",
                GitHubUrl = "https://github.com/codebude/QRCoder",
                CategoryBadge = "Utility",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "CommunityToolkit.Mvvm",
                Role = "Modern .NET MVVM Architecture",
                Description = "High performance, modern .NET Community Toolkit MVVM framework with Roslyn source generators.",
                Author = ".NET Community & Microsoft",
                License = "MIT",
                GitHubUrl = "https://github.com/CommunityToolkit/dotnet",
                CategoryBadge = "Framework",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "Serilog",
                Role = "High-Performance Structured Logging",
                Description = "Diagnostic and structured rolling file logging framework for .NET applications.",
                Author = "Serilog Contributors",
                License = "Apache-2.0",
                GitHubUrl = "https://github.com/serilog/serilog",
                CategoryBadge = "Diagnostics",
                IsCore = false
            },
            new ProjectCredit
            {
                Name = "DepotBox",
                Role = "Depots & Metadata Service",
                Description = "Manifest archive index, game availability status, and metadata REST API service.",
                Author = "DepotBox Team",
                License = "Web Service",
                GitHubUrl = "https://depotbox.org",
                WebsiteUrl = "https://depotbox.org",
                CategoryBadge = "Service",
                IsCore = true
            }
        ];
    }

    /// <summary>
    /// Opens the specified URL in the user's default browser.
    /// </summary>
    [RelayCommand]
    public void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
