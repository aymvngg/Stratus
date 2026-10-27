using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Models;
using Stratus.Core.Services;

namespace Stratus.ViewModels;

/// <summary>One preview row: what the profile asks for and whether it would change anything here.</summary>
public sealed class ProfileRow : ObservableObject
{
    public ProfileRow(ProfileAction action)
    {
        Category = action.Category;
        Name = action.Name;
        ActionText = action.Action;
        WillChange = action.WillChange;
    }

    public string Category { get; }
    public string Name { get; }
    public string ActionText { get; }
    public bool WillChange { get; }
    public string ChangeText => WillChange ? "Will change" : "Skipped";
}

public sealed class ProfileViewModel : ObservableObject
{
    public ObservableCollection<ProfileRow> Preview { get; } = new();

    private string _profilePath = "";
    private string _exportPath = "";
    private string _previewTitle = "";
    private string _summaryText = "";
    private bool _isBusy;
    private string _busyText = "";
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _statusOpen;

    /// <summary>The profile file the user picked or typed. Changes re-enable the Apply button.</summary>
    private StratusProfile? _pending;

    public string ProfilePath
    {
        get => _profilePath;
        set
        {
            if (Set(ref _profilePath, value))
                Raise(nameof(CanApply));
        }
    }

    public string ExportPath
    {
        get => _exportPath;
        private set
        {
            if (Set(ref _exportPath, value))
                Raise(nameof(HasExportPath));
        }
    }

    public bool HasExportPath => !string.IsNullOrWhiteSpace(ExportPath);

    public string PreviewTitle
    {
        get => _previewTitle;
        private set
        {
            if (Set(ref _previewTitle, value))
                Raise(nameof(HasPreview));
        }
    }

    public bool HasPreview => Preview.Count > 0;

    public string SummaryText
    {
        get => _summaryText;
        private set
        {
            if (Set(ref _summaryText, value))
                Raise(nameof(HasSummary));
        }
    }

    public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanInteract), nameof(CanApply));
        }
    }

    public bool CanInteract => !IsBusy;

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public bool CanApply => _pending is not null && !IsBusy;

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }

    private void SetPreview(List<ProfileAction> actions, string title)
    {
        Preview.Clear();
        foreach (var action in actions)
            Preview.Add(new ProfileRow(action));

        PreviewTitle = title;
        Raise(nameof(HasPreview));
    }

    public async Task ExportAsync()
    {
        if (IsBusy)
        {
            ShowStatus("Another action is still running.", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        BusyText = "Reading the current setup...";
        try
        {
            var profile = await AppServices.Profiles.CaptureAsync();
            var json = ProfileService.Serialize(profile);
            var path = ProfileService.DefaultExportPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            ExportPath = path;
            ShowStatus($"Saved {profile.ItemCount} items to {path}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the profile", ex);
            ShowStatus("Could not save the profile: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task ImportAsync(string path)
    {
        path = path?.Trim() ?? "";
        if (path.Length == 0)
        {
            ShowStatus("Choose a profile file first.", InfoBarSeverity.Warning);
            return;
        }

        if (IsBusy)
        {
            ShowStatus("Another action is still running.", InfoBarSeverity.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            ShowStatus("That file does not exist.", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        BusyText = "Reading the profile...";
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var (profile, error) = ProfileService.Deserialize(json);
            if (profile is null)
            {
                ShowStatus(error ?? "That file is not a valid Stratus profile.", InfoBarSeverity.Error);
                return;
            }

            _pending = profile;
            SetPreview(await AppServices.Profiles.PreviewAsync(profile),
                $"Preview for {profile.MachineName} ({profile.ExportedAtUtc.ToLocalTime():g})");
            SummaryText = "";
            ShowStatus("Review the list below, then choose Apply.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the profile", ex);
            ShowStatus("Could not load the profile: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task ApplyAsync()
    {
        if (_pending is null)
        {
            ShowStatus("Load a profile first.", InfoBarSeverity.Warning);
            return;
        }

        if (IsBusy)
        {
            ShowStatus("Another action is still running.", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        BusyText = "Applying the profile...";
        try
        {
            var progress = new Progress<string>(step => BusyText = step);
            var report = await AppServices.Profiles.ApplyAsync(_pending, progress);
            await AppServices.RefreshFromEngineAsync();

            SummaryText = report.Summary;
            var firstFailure = report.Messages.FirstOrDefault(message =>
                message.Contains("failed", StringComparison.OrdinalIgnoreCase));
            var status = report.Summary + (firstFailure is null ? "" : " First failure: " + firstFailure);
            ShowStatus(status, report.Failed == 0
                ? InfoBarSeverity.Success
                : report.Applied > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error);

            // Keep the loaded profile so a failed run can be retried; show what is left to do.
            SetPreview(await AppServices.Profiles.PreviewAsync(_pending), PreviewTitle);
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the profile", ex);
            ShowStatus("Could not apply the profile: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public void ClearPreview()
    {
        _pending = null;
        Preview.Clear();
        SummaryText = "";
        PreviewTitle = "";
        Raise(nameof(HasPreview), nameof(HasSummary), nameof(CanApply));
    }
}
