using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using TrackEditor.Core.Models;
using TrackEditor.Services;

namespace TrackEditor;

public partial class ServerTracksWindow : Window
{
    readonly ServerTrackService _svc;
    readonly Track? _activeTrack;

    /// <summary>Set to the deserialized track when the user opens one; the caller reads it after the dialog closes.</summary>
    public Track? LoadedTrack { get; private set; }

    sealed class TrackRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public bool IsShared { get; init; }
        public DateTime UpdatedUtc { get; init; }
        public string SharedText => IsShared ? "✓" : "";
        public string UpdatedText => UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public ServerTracksWindow(ServerTrackService svc, Track? activeTrack)
    {
        InitializeComponent();
        _svc = svc;
        _activeTrack = activeTrack;
        BtnSaveNew.IsEnabled = activeTrack is not null;
        Title = $"Server Tracks — {svc.Email}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        SetBusy("Loading…");
        try
        {
            var list = await _svc.ListAsync();
            TrackList.ItemsSource = list.Select(t => new TrackRow
            {
                Id = t.Id,
                Name = t.Name,
                IsShared = t.IsShared,
                UpdatedUtc = t.UpdatedUtc,
            }).ToList();
            TxtStatus.Text = $"{list.Count} track(s) on server";
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
        finally { BtnRefresh.IsEnabled = true; }
    }

    void SetBusy(string msg) { TxtStatus.Text = msg; BtnRefresh.IsEnabled = false; }

    TrackRow? Selected => TrackList.SelectedItem as TrackRow;

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool sel = Selected is not null;
        BtnOpen.IsEnabled = sel;
        BtnDelete.IsEnabled = sel;
        BtnShare.IsEnabled = sel;
        BtnUnshare.IsEnabled = sel;
        BtnUpdate.IsEnabled = sel && _activeTrack is not null;
    }

    private void TrackList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) Open_Click(sender, e);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        SetBusy("Opening…");
        try
        {
            var dto = await _svc.GetAsync(row.Id);
            if (dto is null) { TxtStatus.Text = "Track not found."; return; }
            var track = _svc.JsonToTrack(dto.TrackJson);
            if (track is null) { TxtStatus.Text = "Could not parse track data."; return; }
            track.ServerId = dto.Id;
            LoadedTrack = track;
            DialogResult = true;
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void SaveNew_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTrack is null) return;
        SetBusy("Saving…");
        try
        {
            var dto = await _svc.CreateAsync(_activeTrack.Name, _svc.TrackToJson(_activeTrack));
            if (dto is null) { TxtStatus.Text = "Save failed."; return; }
            _activeTrack.ServerId = dto.Id;
            await RefreshAsync();
            TxtStatus.Text = $"Saved as \"{dto.Name}\".";
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || _activeTrack is null) return;
        var confirm = MessageBox.Show(this,
            $"Overwrite server track \"{row.Name}\" with the active track \"{_activeTrack.Name}\"?",
            "Update Track", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        SetBusy("Updating…");
        try
        {
            await _svc.UpdateAsync(row.Id, _activeTrack.Name, _svc.TrackToJson(_activeTrack));
            _activeTrack.ServerId = row.Id;
            await RefreshAsync();
            TxtStatus.Text = "Updated.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var confirm = MessageBox.Show(this, $"Delete \"{row.Name}\" from the server?",
            "Delete Track", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        SetBusy("Deleting…");
        try
        {
            await _svc.DeleteAsync(row.Id);
            await RefreshAsync();
            TxtStatus.Text = "Deleted.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        SetBusy("Sharing…");
        try
        {
            await _svc.ShareAsync(row.Id);
            await RefreshAsync();
            TxtStatus.Text = "Track is now shared.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void Unshare_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        SetBusy("Unsharing…");
        try
        {
            await _svc.UnshareAsync(row.Id);
            await RefreshAsync();
            TxtStatus.Text = "Track is now private.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Error: {ex.Message}"; }
    }
}
