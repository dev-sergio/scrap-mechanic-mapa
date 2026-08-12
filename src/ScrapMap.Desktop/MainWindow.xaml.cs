using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using ScrapMap.Core;

namespace ScrapMap.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DispatcherTimer _autoRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };

    private UuidCatalog? _uuidCatalog;
    private SaveFileStamp? _lastSaveStamp;
    private bool _isInitialized;
    private bool _isLoading;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_OnLoaded;
        Closed += (_, _) => _autoRefreshTimer.Stop();
        _autoRefreshTimer.Tick += AutoRefreshTimer_OnTick;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowLoading("Localizando saves e dados do jogo…");
            var gameRoot = GameLocator.FindInstallation();
            _uuidCatalog = await Task.Run(() => UuidCatalog.Load(gameRoot));

            var saves = SaveLocator.FindSurvivalSaves()
                .Select(file => new SaveOption(file))
                .ToArray();
            SaveComboBox.ItemsSource = saves;

            await MapView.EnsureCoreWebView2Async();
            MapView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            MapView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "appassets.scrapmap",
                AppContext.BaseDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
            if (saves.Length == 0)
            {
                _isInitialized = true;
                StatusText.Text = "Nenhum save Survival foi encontrado.";
                MapView.NavigateToString(MapHtmlBuilder.BuildEmpty("Nenhum save Survival encontrado"));
                return;
            }

            SaveComboBox.SelectedIndex = 0;
            _isInitialized = true;
            await LoadSelectedSaveAsync(preserveMapState: false, suppressErrors: false);
            _autoRefreshTimer.Start();
        }
        catch (Exception exception)
        {
            ShowError(exception, replaceMap: true);
        }
        finally
        {
            HideLoading();
        }
    }

    private async Task LoadSelectedSaveAsync(bool preserveMapState, bool suppressErrors)
    {
        if (!_isInitialized
            || _isLoading
            || SaveComboBox.SelectedItem is not SaveOption selected
            || _uuidCatalog is null)
        {
            return;
        }

        _isLoading = true;
        var viewState = preserveMapState ? await CaptureMapStateAsync() : null;
        ShowLoading($"Lendo {selected.File.Name}…");
        try
        {
            using var safeSnapshot = await SafeSaveSnapshot.CreateAsync(selected.File.FullName);
            var reader = new ScrapSaveReader(_uuidCatalog);
            var snapshot = await reader.ReadAsync(safeSnapshot.DatabasePath);
            snapshot = snapshot with { SavePath = selected.File.FullName };
            var terrain = TerrainOverlayLoader.TryLoad(snapshot.Game.Seed);
            MapView.NavigateToString(MapHtmlBuilder.Build(snapshot, terrain, viewState));
            _lastSaveStamp = SaveFileStamp.Read(selected.File.FullName);
            var terrainStatus = terrain is null
                ? "terreno não extraído"
                : $"mundo completo {terrain.WorldXMax - terrain.WorldXMin + 1}×{terrain.WorldYMax - terrain.WorldYMin + 1} células";
            StatusText.Text = $"{selected.File.Name} · seed {snapshot.Game.Seed} · {terrainStatus} · {snapshot.ExploredCells.Count:N0} células persistidas · {snapshot.Resources.Count:N0} recursos · {snapshot.Creations.Count:N0} construções · atualizado {DateTime.Now:T}";
        }
        catch (SqliteException exception) when (suppressErrors && exception.SqliteErrorCode is 5 or 6)
        {
            StatusText.Text = "O jogo está gravando o save; tentando novamente…";
        }
        catch (IOException) when (suppressErrors)
        {
            StatusText.Text = "O save está ocupado; tentando novamente…";
        }
        catch (Exception exception) when (suppressErrors)
        {
            StatusText.Text = $"Atualização adiada: {exception.Message}";
        }
        catch (Exception exception)
        {
            ShowError(exception, replaceMap: !preserveMapState);
        }
        finally
        {
            _isLoading = false;
            HideLoading();
        }
    }

    private async Task<MapViewState?> CaptureMapStateAsync()
    {
        if (MapView.CoreWebView2 is null) return null;
        try
        {
            var result = await MapView.CoreWebView2.ExecuteScriptAsync(
                "window.scrapMapGetState ? JSON.stringify(window.scrapMapGetState()) : null");
            if (string.IsNullOrWhiteSpace(result) || result == "null") return null;
            var stateJson = JsonSerializer.Deserialize<string>(result);
            return string.IsNullOrWhiteSpace(stateJson)
                ? null
                : JsonSerializer.Deserialize<MapViewState>(stateJson, StateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async void AutoRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        if (AutoRefreshCheckBox.IsChecked != true
            || _isLoading
            || SaveComboBox.SelectedItem is not SaveOption selected)
        {
            return;
        }

        var currentStamp = SaveFileStamp.Read(selected.File.FullName);
        if (_lastSaveStamp is not null && currentStamp != _lastSaveStamp)
        {
            await LoadSelectedSaveAsync(preserveMapState: true, suppressErrors: true);
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        await LoadSelectedSaveAsync(preserveMapState: true, suppressErrors: false);

    private async void SaveComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _lastSaveStamp = null;
        await LoadSelectedSaveAsync(preserveMapState: false, suppressErrors: false);
    }

    private void ShowLoading(string message)
    {
        LoadingText.Text = message;
        LoadingPanel.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        SaveComboBox.IsEnabled = false;
        AutoRefreshCheckBox.IsEnabled = false;
    }

    private void HideLoading()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = true;
        SaveComboBox.IsEnabled = true;
        AutoRefreshCheckBox.IsEnabled = true;
    }

    private void ShowError(Exception exception, bool replaceMap)
    {
        StatusText.Text = "Não foi possível carregar o mapa.";
        if (_isInitialized && replaceMap)
        {
            MapView.NavigateToString(MapHtmlBuilder.BuildEmpty("Erro ao ler o save", exception.Message));
        }
        MessageBox.Show(this, exception.Message, "ScrapMap", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed record SaveOption(FileInfo File)
    {
        public string DisplayName => $"{Path.GetFileNameWithoutExtension(File.Name)}  ·  {File.LastWriteTime:g}";
    }

    private sealed record SaveFileStamp(
        long DatabaseWriteTicks,
        long DatabaseLength,
        long WalWriteTicks,
        long WalLength,
        long JournalWriteTicks,
        long JournalLength)
    {
        public static SaveFileStamp Read(string savePath)
        {
            var database = ReadPart(savePath);
            var wal = ReadPart(savePath + "-wal");
            var journal = ReadPart(savePath + "-journal");
            return new SaveFileStamp(database.Ticks, database.Length, wal.Ticks, wal.Length, journal.Ticks, journal.Length);
        }

        private static (long Ticks, long Length) ReadPart(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc.Ticks, file.Length) : (0, 0);
        }
    }
}
