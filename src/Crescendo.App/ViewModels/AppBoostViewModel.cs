using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Crescendo.Models;
using Crescendo.Services;

namespace Crescendo.ViewModels;

/// <summary>One row in the per-application mixer.</summary>
public sealed class AppBoostViewModel : ObservableObject
{
    private readonly AppBoost _model;
    private readonly Action _onChanged;
    private bool _isActive;

    public AppBoostViewModel(AppBoost model, AudioSessionInfo? session, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        _isActive = session?.IsActive ?? false;
        ExecutablePath = session?.ExecutablePath;
        Icon = IconLoader.Load(ExecutablePath);
    }

    public string Executable => _model.Executable;
    public string DisplayName => _model.DisplayName;
    public string? ExecutablePath { get; }
    public ImageSource? Icon { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Status line under the app name.</summary>
    public string StateText => IsActive ? "Playing" : "Idle";

    public double BoostPercent
    {
        get => Math.Round(_model.Boost * 100.0);
        set
        {
            float boost = (float)Math.Clamp(value / 100.0, 0.0, 5.0);
            if (Math.Abs(_model.Boost - boost) < 0.001f) return;
            _model.Boost = boost;
            Raise();
            Raise(nameof(BoostText));
            _onChanged();
        }
    }

    public string BoostText => $"{BoostPercent:0}%";

    public bool Muted
    {
        get => _model.Muted;
        set
        {
            if (_model.Muted == value) return;
            _model.Muted = value;
            Raise();
            _onChanged();
        }
    }

    public void Reset()
    {
        _model.Boost = 1.0f;
        _model.Muted = false;
        Raise(nameof(BoostPercent));
        Raise(nameof(BoostText));
        Raise(nameof(Muted));
        _onChanged();
    }

    public void UpdateActivity(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        Raise(nameof(IsActive));
        Raise(nameof(StateText));
    }
}

internal static class IconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the executable's icon for the mixer list. Failures are cached as
    /// null so a protected or missing file is not probed on every refresh.
    /// </summary>
    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out ImageSource? cached)) return cached;

        ImageSource? result = null;
        try
        {
            if (File.Exists(path))
            {
                using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    result.Freeze();
                }
            }
        }
        catch (Exception)
        {
            result = null;
        }

        Cache[path] = result;
        return result;
    }
}
