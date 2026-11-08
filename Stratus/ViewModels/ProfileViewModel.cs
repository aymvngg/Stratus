using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Coordination;
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

    /// <summary>
    /// The loaded profile is the only thing Apply reads. It is replaced, never mutated, and
    /// cleared only by the user, so a running apply always sees the profile it previewed.
    /// </summary>
    private StratusProfile? _loaded;
    private string _loadedTitle = "";

    public string ProfilePath
    {
        get => _profilePath;
        set => Set(ref _profilePath, value);
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
                Raise(nameof(CanInteract), nameof(CanApply), nameof(CanCancel));
        }
    }

    public bool CanInteract => !IsBusy;

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }
    public bool StatusOpen { get => _statusOpen; set => Set(ref _statusOpen, value); }

    public bool CanApply => _loaded is not null && !IsBusy;

    /// <summary>Cancel stays disabled during an apply: it must not clear the profile mid-run.</summary>
    public bool CanCancel => !IsBusy;

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

    public async Task<OperationOutcome> ExportAsync()
    {
        if (IsBusy)
            return OperationOutcome.Warning("Another action is still running.");

        IsBusy = true;
        BusyText = "Reading the current setup...";
        try
        {
            var profile = await Task.Run(() => AppServices.Profiles.CaptureAsync());
            var json = ProfileService.Serialize(profile);
            var path = ProfileService.DefaultExportPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            ExportPath = path;
            return OperationOutcome.Success($"Saved {profile.ItemCount} items to {path}.");
        }
        catch (ProfileValidationException ex)
        {
            // The capture was refused on purpose: a partial profile would look complete.
            Log.Warn("The profile was not saved: " + ex.Message);
            return OperationOutcome.Warning(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the profile", ex);
            return OperationOutcome.Error("Could not save the profile: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task<OperationOutcome> ImportAsync(string path)
    {
        path = path?.Trim() ?? "";
        if (path.Length == 0)
            return OperationOutcome.Warning("Choose a profile file first.");

        if (IsBusy)
            return OperationOutcome.Warning("Another action is still running.");

        if (!File.Exists(path))
            return OperationOutcome.Warning("That file does not exist.");

        IsBusy = true;
        BusyText = "Reading the profile...";
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var (profile, error) = ProfileService.Deserialize(json);
            if (profile is null)
                return OperationOutcome.Error(error ?? "That file is not a valid Stratus profile.");

            var title = $"Preview for {profile.MachineName} ({profile.ExportedAtUtc.ToLocalTime():g})";
            var preview = await Task.Run(() => AppServices.Profiles.PreviewAsync(profile));

            // Load the profile and its preview together: Apply can only ever see a pair.
            _loaded = profile;
            _loadedTitle = title;
            SetPreview(preview, title);
            SummaryText = "";
            Raise(nameof(CanApply));
            return OperationOutcome.Info("Review the list below, then choose Apply.");
        }
        catch (ProfileValidationException ex)
        {
            Log.Warn("The profile could not be previewed: " + ex.Message);
            return OperationOutcome.Warning(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the profile", ex);
            return OperationOutcome.Error("Could not load the profile: " + ex.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task<OperationOutcome> ApplyAsync()
    {
        var profile = _loaded;
        if (profile is null)
            return OperationOutcome.Warning("Load a profile first.");

        if (IsBusy)
            return OperationOutcome.Warning("Another action is still running.");

        if (!AppServices.TweakWrites.TryEnter())
            return OperationOutcome.Warning("The tweak list is being changed on another page.");

        IsBusy = true;
        BusyText = "Applying the profile...";
        AppServices.StateRevision.Advance();
        try
        {
            var progress = new Progress<string>(step => BusyText = step);
            var report = await Task.Run(() => AppServices.Profiles.ApplyAsync(profile, progress));
            SummaryText = report.Summary;
            var status = report.Summary;
            try
            {
                await AppServices.RefreshFromEngineAsync();
                var preview = await Task.Run(() => AppServices.Profiles.PreviewAsync(profile));
                if (ReferenceEquals(_loaded, profile))
                    SetPreview(preview, _loadedTitle);
            }
            catch (Exception ex)
            {
                Log.Error("The profile finished but its updated state could not be read", ex);
                return OperationOutcome.Warning(status + " The updated state could not be read: " + ex.Message);
            }

            return report.Failed == 0
                ? OperationOutcome.Success(status)
                : report.Applied > 0 ? OperationOutcome.Warning(status) : OperationOutcome.Error(status);
        }
        catch (ProfileValidationException ex)
        {
            Log.Warn("The profile was not applied: " + ex.Message);
            return OperationOutcome.Warning(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the profile", ex);
            return OperationOutcome.Error("Could not apply the profile: " + ex.Message);
        }
        finally
        {
            EndBusy();
            AppServices.TweakWrites.Exit();
        }
    }

    /// <summary>Drops the loaded profile. Disabled in the UI while an apply runs.</summary>
    public void ClearPreview()
    {
        if (IsBusy)
            return;

        _loaded = null;
        _loadedTitle = "";
        Preview.Clear();
        SummaryText = "";
        PreviewTitle = "";
        Raise(nameof(HasPreview), nameof(HasSummary), nameof(CanApply));
    }

    private void EndBusy()
    {
        IsBusy = false;
        BusyText = "";
    }
}
