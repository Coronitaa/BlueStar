using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlueStar.App.ViewModels;
using BlueStar.Infrastructure.Downloader;

namespace BlueStar.App.Controls;

public partial class SteamDownloadGraphControl : UserControl
{
    private const int MaxSamples = 60; // 60 seconds of history
    private readonly List<(double net, double disk)> _samples = new(MaxSamples);
    private readonly DispatcherTimer _renderTimer;

    private double _peakNetSpeed;
    private double _peakDiskSpeed;
    private double _currentNetSpeed;
    private double _currentDiskSpeed;

    public SteamDownloadGraphControl()
    {
        InitializeComponent();

        // Pre-fill samples with zeros
        for (int i = 0; i < MaxSamples; i++)
        {
            _samples.Add((0, 0));
        }

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _renderTimer.Tick += OnRenderTick;

        Loaded += (s, e) =>
        {
            HookTelemetry();
            _renderTimer.Start();
        };

        Unloaded += (s, e) =>
        {
            _renderTimer.Stop();
            UnhookTelemetry();
        };

        DataContextChanged += (s, e) =>
        {
            UnhookTelemetry();
            HookTelemetry();
        };

        SizeChanged += (s, e) => RedrawGraph();
    }

    private DownloadsViewModel? ViewModel => DataContext as DownloadsViewModel;
    private DownloadQueueManager? _hookedQueueManager;

    private void HookTelemetry()
    {
        if (ViewModel?.QueueManager is { } qm)
        {
            _hookedQueueManager = qm;
            qm.SpeedSampleReceived += OnSpeedSampleReceived;
        }
    }

    private void UnhookTelemetry()
    {
        if (_hookedQueueManager != null)
        {
            _hookedQueueManager.SpeedSampleReceived -= OnSpeedSampleReceived;
            _hookedQueueManager = null;
        }
    }

    private void OnSpeedSampleReceived(double netSpeed, double writeSpeed)
    {
        _currentNetSpeed = netSpeed;
        _currentDiskSpeed = writeSpeed;

        if (netSpeed > _peakNetSpeed) _peakNetSpeed = netSpeed;
        if (writeSpeed > _peakDiskSpeed) _peakDiskSpeed = writeSpeed;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        // Sample current speeds
        if (ViewModel?.QueueManager is { } qm)
        {
            _currentNetSpeed = qm.TotalDownloadSpeed;
            _currentDiskSpeed = qm.TotalWriteSpeed;
            if (qm.PeakDownloadSpeed > _peakNetSpeed) _peakNetSpeed = qm.PeakDownloadSpeed;
            if (qm.PeakWriteSpeed > _peakDiskSpeed) _peakDiskSpeed = qm.PeakWriteSpeed;
        }

        // Push new sample and pop oldest
        _samples.Add((_currentNetSpeed, _currentDiskSpeed));
        while (_samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
        }

        UpdateTextMetrics();
        RedrawGraph();
    }

    private void UpdateTextMetrics()
    {
        TxtCurrentNetSpeed.Text = FormatSpeed(_currentNetSpeed);
        TxtPeakNetSpeed.Text = FormatSpeed(_peakNetSpeed);
        TxtCurrentDiskSpeed.Text = FormatSpeed(_currentDiskSpeed);
        TxtPeakDiskSpeed.Text = FormatSpeed(_peakDiskSpeed);

        if (ViewModel?.QueueManager is { } qm)
        {
            var activeJob = qm.Queue.FirstOrDefault(q => q.IsDownloading) ?? qm.Queue.FirstOrDefault(q => q.IsActive);
            if (activeJob != null && activeJob.TotalBytes > 0)
            {
                TxtTotalDownloaded.Text = $"{FormatBytes(activeJob.DownloadedBytes)} / {FormatBytes(activeJob.TotalBytes)}";
            }
            else
            {
                long totalDownloaded = qm.Queue.Sum(q => q.DownloadedBytes);
                TxtTotalDownloaded.Text = FormatBytes(totalDownloaded);
            }
        }
    }

    private void RedrawGraph()
    {
        double width = WaveformCanvas.ActualWidth;
        double height = WaveformCanvas.ActualHeight;

        if (width <= 10 || height <= 10) return;

        // Find max scale (minimum 5 MB/s to prevent tiny spikes taking up full height)
        double maxSample = _samples.Max(s => Math.Max(s.net, s.disk));
        double maxScale = Math.Max(5.0 * 1024 * 1024, maxSample * 1.15);

        // Update Scale Labels
        TxtScaleMax.Text = FormatSpeed(maxScale);
        TxtScaleMid.Text = FormatSpeed(maxScale / 2.0);

        int count = _samples.Count;
        if (count < 2) return;

        double stepX = width / (count - 1);

        var netPoints = new Point[count];
        var diskPoints = new Point[count];

        for (int i = 0; i < count; i++)
        {
            double x = i * stepX;
            double yNet = height - (Math.Clamp(_samples[i].net / maxScale, 0.0, 1.0) * (height - 4)) - 2;
            double yDisk = height - (Math.Clamp(_samples[i].disk / maxScale, 0.0, 1.0) * (height - 4)) - 2;

            netPoints[i] = new Point(x, yNet);
            diskPoints[i] = new Point(x, yDisk);
        }

        // 1. Build Disk Line & Area
        var diskLineGeom = new StreamGeometry();
        using (var ctx = diskLineGeom.Open())
        {
            ctx.BeginFigure(diskPoints[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo(diskPoints.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        diskLineGeom.Freeze();
        DiskLinePath.Data = diskLineGeom;

        var diskAreaGeom = new StreamGeometry();
        using (var ctx = diskAreaGeom.Open())
        {
            ctx.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
            ctx.LineTo(diskPoints[0], isStroked: false, isSmoothJoin: false);
            ctx.PolyLineTo(diskPoints.Skip(1).ToList(), isStroked: false, isSmoothJoin: true);
            ctx.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
        }
        diskAreaGeom.Freeze();
        DiskAreaPath.Data = diskAreaGeom;

        // 2. Build Network Line & Area
        var netLineGeom = new StreamGeometry();
        using (var ctx = netLineGeom.Open())
        {
            ctx.BeginFigure(netPoints[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo(netPoints.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        netLineGeom.Freeze();
        NetLinePath.Data = netLineGeom;

        var netAreaGeom = new StreamGeometry();
        using (var ctx = netAreaGeom.Open())
        {
            ctx.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
            ctx.LineTo(netPoints[0], isStroked: false, isSmoothJoin: false);
            ctx.PolyLineTo(netPoints.Skip(1).ToList(), isStroked: false, isSmoothJoin: true);
            ctx.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
        }
        netAreaGeom.Freeze();
        NetAreaPath.Data = netAreaGeom;
    }

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        > 1024 * 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F1} GB/s",
        > 1024 * 1024        => $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s",
        > 1024               => $"{bytesPerSec / 1024.0:F1} KB/s",
        _                    => $"{bytesPerSec:F0} B/s"
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        > 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        > 1024L * 1024        => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _                     => $"{bytes / 1024.0:F0} KB"
    };
}
