using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft;

[SuppressMessage("Design", "CA1001", Justification = "WPF owns the window lifetime; disposable services are released by the coordinated Closing handler.")]
public partial class MainWindow : Window
{
    private const int MinMemoryGb = MemorySizingService.MinMemoryGb;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SecretLoadingDuration = TimeSpan.FromMinutes(10);
    private const int HostReachabilityAttempts = 3;
    // EB59 is a half-size badge glyph; this maps its ink bounds onto EA18's full shield bounds.
    private static readonly Matrix DisabledVoiceProtectionIconTransform = new(2d, 0d, 0d, 2d, -14.875d, -14d);
    private static readonly TimeSpan HostReachabilityTimeout = TimeSpan.FromMilliseconds(900);

    private readonly ObservableCollection<PeerViewModel> _peers = new();
    private readonly ObservableCollection<PeerViewModel> _hostPeers = new();
    private readonly ObservableCollection<WorldViewModel> _worlds = new();
    private readonly ObservableCollection<ClientBuildViewModel> _builds = new();
    private readonly ObservableCollection<PeerViewModel> _voicePeers = new();
    private readonly ObservableCollection<NetworkProviderOption> _networkProviders = new();
    private readonly Dictionary<string, VoicePresenceEntry> _voicePresence = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _secretTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _networkRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TransferRateTracker _transferRate = new();
    private readonly TransferRateTracker _updateRate = new();
    private readonly TransferRateTracker _runtimeRate = new();

    private AppPaths? _paths;
    private AppSettings? _settings;
    private SettingsService? _settingsService;
    private Logger? _logger;
    private VirtualNetworkService? _network;
    private NetworkToolSetupService? _networkToolSetup;
    private PackHashService? _packHash;
    private WorldMetadataService? _worldMetadata;
    private WorldPlayerProfileService? _worldPlayerProfiles;
    private LocalIdentityService? _identityService;
    private PortableIdentityAdapterService? _identityAdapter;
    private PackInstanceService? _packInstances;
    private PackRuntimeService? _packRuntimes;
    private WaypointSyncService? _waypointSync;
    private SkinService? _skinService;
    private PeerDiscoveryService? _discovery;
    private LanAdvertisementService? _lanAdvertisement;
    private LanRelayService? _lanRelay;
    private MinecraftProcessService? _minecraft;
    private WorldTransferService? _transfer;
    private UpdateService? _updateService;
    private VoiceChannelService? _voiceChannel;
    private VoiceNetworkCoordinator? _voiceNetwork;
    private VoiceSettingsWindow? _voiceSettingsWindow;
    private GlobalPttHotkeyService? _pttHotkey;
    private NetworkEnvironmentSnapshot _networkSnapshot = new();
    private NetworkEndpointInfo? _primaryEndpoint;
    private List<NetworkEndpointInfo> _networkEndpoints = [];
    private string _localPackHash = "";
    private string _state = "Starting";
    private int? _openToLanPort;
    private long _openToLanLogPosition;
    private int? _cachedOpenToLanPort;
    private bool _networkToolInstalled;
    private bool _networkRefreshInProgress;
    private bool _networkChangeSubscribed;
    private bool _networkToolInstallBusy;
    private bool _busy;
    private bool _voiceBusy;
    private bool _suppressTextPersistence;
    private bool _suppressBuildPersistence;
    private bool _suppressMemoryTextChanged;
    private bool _suppressVoicePersistence;
    private bool _suppressNetworkProviderSelection;
    private string _lastVoicePeerListSignature = "";
    private string _lastVoiceTransportPeerSignature = "";
    private PeerViewModel? _localVoicePeer;
    private bool _voicePttInputPressed;
    private bool _voicePttToggleActive;
    private long _transferBytesCurrent;
    private long _transferBytesTotal;
    private double _lastTransferSpeedBytesPerSecond;
    private bool _transferActive;
    private DateTimeOffset? _secretLoadingStartedAt;
    private bool _hostRttScanInProgress;
    private bool _updateBusy;
    private bool _isEditingPlayerName;
    private bool _startupComplete;
    private bool _minecraftRunning;
    private bool _minecraftPreparing;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _restartAfterUpdateOnExit;
    private PreparedUpdate? _preparedUpdate;
    private readonly WindowPlacementService _windowPlacement;

    public MainWindow()
    {
        InitializeComponent();
        _windowPlacement = new WindowPlacementService(new AppPaths(AppPaths.ResolveApplicationRoot()));
        _windowPlacement.Apply(this);
        BuildComboBox.ItemsSource = _builds;
        OnlinePlayerComboBox.ItemsSource = _peers;
        HostComboBox.ItemsSource = _hostPeers;
        WorldComboBox.ItemsSource = _worlds;
        VoicePeersItemsControl.ItemsSource = _voicePeers;
        NetworkProviderComboBox.ItemsSource = _networkProviders;
        _uiTimer.Tick += (_, _) =>
        {
            RefreshBuilds();
            RefreshWorlds();
            RefreshHostPeers();
            PruneStalePeers();
            RefreshHostLatencies();
            RefreshVoicePeers();
            UpdateVoicePeersFromDiscovery();
            RefreshLanAdvertisementState();
            RefreshUi();
        };
        _secretTimer.Tick += (_, _) => RefreshSecretLoadingProgress();
        _networkRefreshTimer.Tick += NetworkRefreshTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            VersionTextBlock.Text = BuildVersionText();
            _paths = new AppPaths(AppPaths.ResolveApplicationRoot());
            _paths.Ensure();
            LogCleanupService.RunCleanup(_paths);
            _settingsService = new SettingsService(_paths);
            _settings = _settingsService.Load();
            _logger = new Logger(_paths.LogFile);
            _logger.LineWritten += line => Dispatcher.Invoke(() => AppendLog(line));
            _network = new VirtualNetworkService(_logger);
            _networkToolSetup = new NetworkToolSetupService(_paths, _logger);
            NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
            _networkChangeSubscribed = true;
            _packHash = new PackHashService(_paths);
            _worldMetadata = new WorldMetadataService();
            _identityService = new LocalIdentityService(_paths);
            _identityAdapter = new PortableIdentityAdapterService(_paths, _logger);
            ResolveAndPersistLocalIdentity();
            _worldPlayerProfiles = new WorldPlayerProfileService(_paths, _logger);
            _packInstances = new PackInstanceService(_paths, _logger);
            _voiceNetwork = new VoiceNetworkCoordinator();
            _packRuntimes = new PackRuntimeService(
                _paths,
                _logger,
                networkCoordinator: _voiceNetwork);
            _waypointSync = new WaypointSyncService(_paths, _logger, _worldMetadata, _network);
            _skinService = new SkinService(_paths, _logger, _network);
            await _skinService.StartAsync(_lifetimeCts.Token);
            _lanRelay = new LanRelayService(_logger, _network);
            _minecraft = new MinecraftProcessService(_paths, _logger, _identityService, _identityAdapter, _worldPlayerProfiles, _packInstances, _packRuntimes, _waypointSync, _skinService);
            _minecraft.ClientRunningChanged += OnMinecraftClientRunningChanged;
            _minecraft.ClientPreparingChanged += OnMinecraftClientPreparingChanged;
            _transfer = new WorldTransferService(_paths, _logger, _minecraft, _settingsService, _worldMetadata, _identityService, _worldPlayerProfiles, _waypointSync, _skinService, _lanRelay, _voiceNetwork, _network);
            _updateService = new UpdateService(
                _paths,
                _logger,
                networkCoordinator: _voiceNetwork);
            _transfer.StatusChanged += message => Dispatcher.Invoke(() => SetState(message));
            _transfer.ProgressChanged += progress =>
            {
                Dispatcher.Invoke(() => ApplyTransferProgress(progress));
            };
            _transfer.BecameHost += () => Dispatcher.Invoke(() =>
            {
                SetState("World received");
                RefreshWorlds();
                RefreshUi();
            });
            _discovery = new PeerDiscoveryService(_paths, _logger, _network);
            _discovery.PeerUpdated += announcement => Dispatcher.Invoke(() => ApplyPeer(announcement));
            _lanAdvertisement = new LanAdvertisementService(_logger, _lanRelay);
            _lanAdvertisement.Start();
            _voiceChannel = new VoiceChannelService(_logger, _voiceNetwork, network: _network);
            _voiceChannel.Initialize(_settings);
            _voiceChannel.SpeakingStateChanged += OnVoiceSpeakingStateChanged;
            _voiceChannel.PeerPresenceChanged += OnVoicePeerPresenceChanged;
            _voiceChannel.TrafficProtectionChanged += OnVoiceTrafficProtectionChanged;
            InitializePttHotkey();

            LoadSettingsIntoUi();
            RefreshVoiceDevices();
            RefreshBuilds();
            RefreshNetworkToolSetupStatus();
            if (_settings.NetworkToolAutoLaunch)
            {
                await AutoLaunchInstalledNetworkToolAsync();
            }
            CaptureNetworkProviders();
            RefreshNetworkEnvironment();
            RefreshHostPeers();
            RefreshMemoryText(saveIfChanged: true);
            RefreshWorlds();
            StartSecretLoading();
            InitializeUpdateUi();
            InitializeRuntimeProgressUi();
            _ = CheckForUpdatesAsync(_lifetimeCts.Token);
            _uiTimer.Start();
            SetState("Ready");
            _logger.Info("Minecraft portable launcher started.");
            await RefreshPackHashAsync(_lifetimeCts.Token);
            await StartNetworkingAsync();
            _startupComplete = true;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _windowPlacement.Save(this);

        // Always leave the original Closing event before issuing the final Close().
        // Several services can complete synchronously when networking was never started.
        await Dispatcher.Yield(DispatcherPriority.Background);

        _uiTimer.Stop();
        _secretTimer.Stop();
        _networkRefreshTimer.Stop();
        if (_networkChangeSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            _networkChangeSubscribed = false;
        }
        _lifetimeCts.Cancel();
        try
        {
            if (_discovery is not null) await _discovery.DisposeAsync();
            if (_lanAdvertisement is not null) await _lanAdvertisement.DisposeAsync();
            _pttHotkey?.Dispose();
            _voiceSettingsWindow?.Close();
            if (_voiceChannel is not null) await _voiceChannel.DisposeAsync();
            if (_transfer is not null) await _transfer.DisposeAsync();
            if (_lanRelay is not null) await _lanRelay.DisposeAsync();
            if (_waypointSync is not null) await _waypointSync.DisposeAsync();
            if (_skinService is not null) await _skinService.DisposeAsync();
            _packInstances?.Dispose();
            _packRuntimes?.Dispose();
            _identityAdapter?.Dispose();
            _packHash?.Dispose();
            _lifetimeCts.Dispose();
        }
        finally
        {
            if (_paths is not null)
            {
                LogCleanupService.ScheduleCurrentExtractionCleanup(_paths, Environment.ProcessId);
            }
            try
            {
                if (_updateService is not null)
                {
                    var prepared = await Task.Run(_updateService.TryGetPreparedUpdate);
                    if (prepared is not null)
                    {
                        var mode = _restartAfterUpdateOnExit
                            ? UpdateInstallMode.InstallAndRestart
                            : UpdateInstallMode.InstallOnExit;
                        _updateService.StartInstall(prepared, mode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Update could not be scheduled during shutdown: {ex.Message}");
            }
            _shutdownComplete = true;
            Close();
        }
    }

    private void LoadSettingsIntoUi()
    {
        var settings = RequireSettings();
        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = RequireSettings().PlayerName;
            RefreshNetworkCredentialsText();
            NetworkToolAutoLaunchCheckBox.IsChecked = settings.NetworkToolAutoLaunch;
            VoiceMasterVolumeSlider.Value = settings.VoiceOutputVolume;
            VoiceMuteButton.Content = "Микрофон";
            VoiceDeafenButton.Content = "Звук";
        }
        finally
        {
            _suppressTextPersistence = false;
        }
    }

    private void RefreshNetworkEnvironment()
    {
        _networkSnapshot = RequireNetwork().GetSnapshot();
        _networkEndpoints = _networkSnapshot.Endpoints.ToList();
        _primaryEndpoint = _networkSnapshot.PrimaryEndpoint;
        var preferredProvider = _primaryEndpoint?.ProviderId;
        foreach (var peer in _peers)
        {
            peer.SetPreferredProvider(preferredProvider);
        }
    }

    private void CaptureNetworkProviders()
    {
        var providers = RequireNetwork().CaptureActiveProviders();
        _suppressNetworkProviderSelection = true;
        try
        {
            _networkProviders.Clear();
            foreach (var provider in providers) _networkProviders.Add(provider);
            NetworkProviderComboBox.SelectedItem = _networkProviders.FirstOrDefault();
        }
        finally
        {
            _suppressNetworkProviderSelection = false;
        }
        NetworkProviderPlaceholderText.Visibility = _networkProviders.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_networkProviders.Count > 0)
        {
            RequireLogger().Info($"Network provider selected for this session: {_networkProviders[0].DisplayName}.");
        }
        else
        {
            RequireLogger().Info("No running VPN client with a usable adapter was detected; physical LAN remains available.");
        }
    }

    private void NetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_shutdownStarted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownStarted) return;
            _networkRefreshTimer.Stop();
            _networkRefreshTimer.Start();
        });
    }

    private async void NetworkRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _networkRefreshTimer.Stop();
        await RefreshNetworkAdaptersSafelyAsync(forceRestart: false);
    }

    private async Task RefreshNetworkAdaptersSafelyAsync(bool forceRestart)
    {
        try
        {
            await RefreshNetworkAdaptersAsync(forceRestart);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Network adapter refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshNetworkAdaptersAsync(bool forceRestart)
    {
        if (_networkRefreshInProgress || _network is null || _settings is null)
        {
            return;
        }

        _networkRefreshInProgress = true;
        try
        {
            var previousFingerprint = _networkSnapshot.Fingerprint;
            RefreshNetworkEnvironment();
            var currentFingerprint = _networkSnapshot.Fingerprint;
            if (_startupComplete &&
                (forceRestart || !string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal)))
            {
                await StartNetworkingAsync();
            }

            RefreshNetworkToolSetupStatus();
            RefreshVoicePeers();
            RefreshLanAdvertisementState();
            RefreshUi();
        }
        finally
        {
            _networkRefreshInProgress = false;
        }
    }

    private async Task RefreshPackHashAsync(CancellationToken token = default)
    {
        if (!token.CanBeCanceled) token = _lifetimeCts.Token;
        var settings = RequireSettings();
        var paths = RequirePaths();
        var relativePath = settings.ClientRelativePath;
        var hash = await RequirePackHash().CalculateAsync(paths.CombineUnderPacks(relativePath), token);
        if (!string.Equals(RequireSettings().ClientRelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return;
        _localPackHash = hash;
        PackHashText.Text = _localPackHash.Length > 12 ? _localPackHash[..12] : _localPackHash;
    }

    private void RefreshBuilds()
    {
        if (_paths is null || _settings is null || _settingsService is null) return;

        Directory.CreateDirectory(_paths.Packs);
        var selectedRelativePath = (BuildComboBox.SelectedItem as ClientBuildViewModel)?.RelativePath;
        var builds = Directory.EnumerateDirectories(_paths.Packs)
            .Where(MinecraftProcessService.HasPackData)
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new ClientBuildViewModel
            {
                Name = Path.GetFileName(path),
                RelativePath = Path.GetRelativePath(_paths.Packs, path),
                FullPath = path
            })
            .ToList();

        var buildPathsMatch = _builds.Select(build => build.RelativePath)
            .SequenceEqual(builds.Select(build => build.RelativePath), StringComparer.OrdinalIgnoreCase);
        if (!buildPathsMatch)
        {
            _suppressBuildPersistence = true;
            try
            {
                _builds.Clear();
                foreach (var build in builds)
                {
                    _builds.Add(build);
                }
            }
            finally
            {
                _suppressBuildPersistence = false;
            }
        }

        var preferredRelativePath = _settings.ClientRelativePath;
        var selectedBuild = _builds.FirstOrDefault(build =>
                string.Equals(build.RelativePath, preferredRelativePath, StringComparison.OrdinalIgnoreCase)) ??
            _builds.FirstOrDefault(build =>
                string.Equals(build.RelativePath, selectedRelativePath, StringComparison.OrdinalIgnoreCase)) ??
            _builds.FirstOrDefault();

        _suppressBuildPersistence = true;
        try
        {
            BuildComboBox.SelectedItem = selectedBuild;
        }
        finally
        {
            _suppressBuildPersistence = false;
        }
        if (selectedBuild is null)
        {
            BuildComboBox.SelectedItem = null;
        }

        if (selectedBuild is not null &&
            !string.Equals(_settings.ClientRelativePath, selectedBuild.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ClientRelativePath = selectedBuild.RelativePath;
            _settingsService.Save(_settings);
            _ = RefreshPackHashAndNetworkingAsync();
        }
    }

    private void RefreshWorlds()
    {
        if (_paths is null || _settings is null || _settingsService is null) return;

        if (!Directory.Exists(_paths.Worlds))
        {
            _worlds.Clear();
            WorldComboBox.SelectedItem = null;
            RefreshUi();
            return;
        }

        var selectedPath = (WorldComboBox.SelectedItem as WorldViewModel)?.Path;
        var metadataContext = CreateWorldMetadataContext();
        var worlds = Directory.EnumerateDirectories(_paths.Worlds)
            .Where(WorldTransferService.IsMinecraftWorldDirectory)
            .Where(path => !Path.GetFileName(path).Contains(".backup-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path => CreateWorldViewModel(path, metadataContext))
            .ToList();

        var worldsMatch = _worlds.Count == worlds.Count &&
            _worlds.Zip(worlds).All(pair =>
                string.Equals(pair.First.Path, pair.Second.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal));
        if (!worldsMatch)
        {
            _worlds.Clear();
            foreach (var world in worlds)
            {
                _worlds.Add(world);
            }
        }

        var savedPath = string.IsNullOrWhiteSpace(_settings.SelectedWorldRelativePath)
            ? null
            : Path.GetFullPath(Path.Combine(_paths.Worlds, _settings.SelectedWorldRelativePath));
        var selectedWorld = _worlds.FirstOrDefault(world =>
                savedPath is not null && string.Equals(world.Path, savedPath, StringComparison.OrdinalIgnoreCase)) ??
            _worlds.FirstOrDefault(world =>
                string.Equals(world.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ??
            _worlds.FirstOrDefault();

        WorldComboBox.SelectedItem = selectedWorld;
        if (selectedWorld is not null)
        {
            var relativePath = Path.GetRelativePath(_paths.Worlds, selectedWorld.Path);
            if (!string.Equals(_settings.SelectedWorldRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SelectedWorldRelativePath = relativePath;
                _settingsService.Save(_settings);
            }
        }
    }

    private void RefreshVoiceDevices()
    {
        if (_voiceChannel is null || _settings is null || _settingsService is null)
        {
            return;
        }

        var inputDevices = _voiceChannel.GetInputDevices();
        var outputDevices = _voiceChannel.GetOutputDevices();

        VoiceInputComboBox.ItemsSource = inputDevices;
        VoiceOutputComboBox.ItemsSource = outputDevices;

        var selectedInput = inputDevices.FirstOrDefault(device =>
            string.Equals(device.Id, _settings.VoiceInputDeviceId, StringComparison.OrdinalIgnoreCase));
        var selectedOutput = outputDevices.FirstOrDefault(device =>
            string.Equals(device.Id, _settings.VoiceOutputDeviceId, StringComparison.OrdinalIgnoreCase));
        var actualInput = selectedInput ?? (inputDevices.Count > 0 ? inputDevices[0] : null);
        var actualOutput = selectedOutput ?? (outputDevices.Count > 0 ? outputDevices[0] : null);
        var inputId = actualInput?.Id ?? "";
        var outputId = actualOutput?.Id ?? "";
        var settingsChanged =
            !string.Equals(_settings.VoiceInputDeviceId, inputId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_settings.VoiceOutputDeviceId, outputId, StringComparison.OrdinalIgnoreCase);

        _suppressVoicePersistence = true;
        try
        {
            VoiceInputComboBox.SelectedItem = actualInput;
            VoiceOutputComboBox.SelectedItem = actualOutput;
            _settings.VoiceInputDeviceId = inputId;
            _settings.VoiceOutputDeviceId = outputId;
            _voiceChannel.SetDeviceIds(inputId, outputId);
        }
        finally
        {
            _suppressVoicePersistence = false;
        }

        if (settingsChanged)
        {
            _settingsService.Save(_settings);
        }

        RefreshVoiceSettingsWindow();
    }

    internal void RefreshVoiceSettingsWindow(VoiceSettingsWindow? window = null)
    {
        var target = window ?? _voiceSettingsWindow;
        if (target is null || _settings is null || _voiceChannel is null)
        {
            return;
        }

        var inputDevices = _voiceChannel.GetInputDevices();
        var outputDevices = _voiceChannel.GetOutputDevices();
        target.ApplyState(
            inputDevices,
            outputDevices,
            _settings.VoiceInputDeviceId,
            _settings.VoiceOutputDeviceId,
            _settings.VoicePttMode,
            PttInputBinding.Parse(_settings.VoicePushToTalkBinding).DisplayName,
            _settings.VoiceInputVolume,
            _settings.VoiceOutputVolume);
    }

    internal void ClearVoiceSettingsWindow(VoiceSettingsWindow window)
    {
        if (ReferenceEquals(_voiceSettingsWindow, window))
        {
            _voiceSettingsWindow = null;
        }
    }

    internal void SetVoiceInputDevice(VoiceAudioDevice device)
    {
        if (_settings is null || _settingsService is null || _voiceChannel is null) return;

        _settings.VoiceInputDeviceId = device.Id;
        _settingsService.Save(_settings);
        _voiceChannel.SetDeviceIds(_settings.VoiceInputDeviceId, _settings.VoiceOutputDeviceId);
        RefreshVoiceDevices();
        RefreshUi();
    }

    internal void SetVoiceOutputDevice(VoiceAudioDevice device)
    {
        if (_settings is null || _settingsService is null || _voiceChannel is null) return;

        _settings.VoiceOutputDeviceId = device.Id;
        _settingsService.Save(_settings);
        _voiceChannel.SetDeviceIds(_settings.VoiceInputDeviceId, _settings.VoiceOutputDeviceId);
        RefreshVoiceDevices();
        RefreshUi();
    }

    internal void SetVoiceMasterVolume(double volume)
    {
        SetVoiceOutputVolume(volume);
    }

    internal void SetVoiceInputVolume(double volume)
    {
        if (_settings is null || _settingsService is null || _voiceChannel is null) return;

        _settings.VoiceInputVolume = Math.Clamp(volume, 0d, 2d);
        _settingsService.Save(_settings);
        _voiceChannel.SetInputVolume(_settings.VoiceInputVolume);
        RefreshVoiceSettingsWindow();
    }

    internal void SetVoiceOutputVolume(double volume)
    {
        if (_settings is null || _settingsService is null || _voiceChannel is null) return;

        _settings.VoiceOutputVolume = Math.Clamp(volume, 0d, 2d);
        _settings.VoiceMasterVolume = _settings.VoiceOutputVolume;
        _settingsService.Save(_settings);
        _voiceChannel.SetOutputVolume(_settings.VoiceOutputVolume);
        VoiceMasterVolumeSlider.Value = _settings.VoiceOutputVolume;
        RefreshVoiceSettingsWindow();
    }

    internal void SetVoicePttMode(string mode)
    {
        if (_settings is null || _settingsService is null) return;

        _settings.VoicePttMode = mode is "Hold" or "Toggle" ? mode : "Off";
        _voicePttInputPressed = false;
        _voicePttToggleActive = false;
        _settingsService.Save(_settings);
        ApplyVoiceTransmissionState();
        RefreshVoiceSettingsWindow();
    }

    internal void ToggleVoiceMute()
    {
        if (_settings is null || _settingsService is null || _voiceChannel is null) return;

        _settings.VoiceMuted = !_voiceChannel.IsMuted;
        _settingsService.Save(_settings);
        _voiceChannel.SetMuted(_settings.VoiceMuted);
        if (_localVoicePeer is not null)
        {
            _localVoicePeer.IsVoiceMuted = _settings.VoiceMuted;
        }
        RefreshVoicePeers();
        RefreshUi();
    }

    internal void ToggleVoiceDeafen()
    {
        if (_settings is null || _settingsService is null || _voiceChannel is null) return;

        _settings.VoiceDeafened = !_settings.VoiceDeafened;
        _settingsService.Save(_settings);
        _voiceChannel.SetDeafened(_settings.VoiceDeafened);
        RefreshUi();
    }

    internal void SetVoicePeerVolume(PeerViewModel peer, double volume)
    {
        if (_voiceChannel is null) return;

        var peerId = ResolveVoicePeerId(peer);
        if (string.IsNullOrWhiteSpace(peerId))
        {
            return;
        }

        var clamped = Math.Clamp(volume, 0d, 2d);
        peer.VoiceVolume = clamped;
        _voiceChannel.SetPeerVolume(peerId, clamped);
        RefreshVoiceSettingsWindow();
    }

    internal void SetVoicePushToTalkKey(Key key)
    {
        if (_settings is null || _settingsService is null) return;

        if (key == Key.None || key == Key.System)
        {
            key = Key.V;
        }

        _settings.VoicePushToTalkKey = key.ToString();
        _settings.VoicePushToTalkBinding = $"Key:{key}";
        _settingsService.Save(_settings);
        _pttHotkey?.SetBinding(_settings.VoicePushToTalkBinding);
        RefreshVoiceSettingsWindow();
    }

    internal void SetVoicePushToTalkBinding(string binding)
    {
        if (_settings is null || _settingsService is null) return;

        var parsed = PttInputBinding.Parse(binding);
        _settings.VoicePushToTalkBinding = parsed.ToString();
        if (parsed.Kind == PttInputKind.Key)
        {
            _settings.VoicePushToTalkKey = parsed.Key.ToString();
        }

        _voicePttInputPressed = false;
        _voicePttToggleActive = false;
        _settingsService.Save(_settings);
        _pttHotkey?.SetBinding(_settings.VoicePushToTalkBinding);
        ApplyVoiceTransmissionState();
        RefreshVoiceSettingsWindow();
    }

    private void InitializePttHotkey()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            _pttHotkey?.Dispose();
            _pttHotkey = new GlobalPttHotkeyService(_settings.VoicePushToTalkBinding, pressed =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnVoicePttInput(pressed);
                }));
            });
        }
        catch (Exception ex)
        {
            _logger?.Warn("Global PTT hotkey unavailable: " + ex.Message);
        }
    }

    private void OnVoicePttInput(bool pressed)
    {
        if (_settings is null)
        {
            return;
        }

        if (_settings.VoicePttMode == "Hold")
        {
            _voicePttInputPressed = pressed;
            ApplyVoiceTransmissionState();
            return;
        }

        if (_settings.VoicePttMode == "Toggle" && pressed)
        {
            _voicePttToggleActive = !_voicePttToggleActive;
            ApplyVoiceTransmissionState();
        }
    }

    private void ApplyVoiceTransmissionState()
    {
        if (_voiceChannel is null || _settings is null)
        {
            return;
        }

        var shouldTransmit = _settings.VoicePttMode switch
        {
            "Hold" => _voicePttInputPressed,
            "Toggle" => _voicePttToggleActive,
            _ => true
        };
        _voiceChannel.SetPttPressed(shouldTransmit);
    }

    private void RefreshHostPeers()
    {
        var selectedIp = (HostComboBox.SelectedItem as PeerViewModel)?.NetworkAddress;
        var hosts = _peers
            .Where(peer => peer.IsHost)
            .OrderByDescending(peer => peer.LastSeen)
            .ThenBy(peer => peer.LastRttMs ?? int.MaxValue)
            .ThenBy(peer => peer.PlayerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _hostPeers.Clear();
        foreach (var host in hosts)
        {
            _hostPeers.Add(host);
        }

        if (_hostPeers.Count == 0)
        {
            HostComboBox.SelectedItem = null;
        }
        else
        {
            var restored = _hostPeers.FirstOrDefault(host => string.Equals(host.NetworkAddress, selectedIp, StringComparison.OrdinalIgnoreCase));
            HostComboBox.SelectedItem = restored ?? _hostPeers[0];
        }

    }

    private void RefreshVoicePeers()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
        foreach (var stale in _voicePresence.Where(pair => pair.Value.LastSeenUtc < cutoff).Select(pair => pair.Key).ToArray())
        {
            _voicePresence.Remove(stale);
        }

        var peers = _voicePresence.Values
            .OrderByDescending(entry => entry.LastSeenUtc)
            .ThenBy(entry => entry.Peer.PlayerName, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry.Peer)
            .ToList();
        if (_voiceChannel is { IsJoined: true })
        {
            var identity = ResolveActiveLocalIdentity();
            _localVoicePeer ??= new PeerViewModel { IsLocalVoicePeer = true };
            _localVoicePeer.PlayerName = identity.name;
            _localVoicePeer.IdentityName = identity.name;
            _localVoicePeer.IdentityId = identity.id;
            _localVoicePeer.SetLocalEndpoints(_networkEndpoints, _primaryEndpoint);
            _localVoicePeer.IsInVoiceChannel = true;
            _localVoicePeer.IsVoiceMuted = _voiceChannel.IsMuted;
            _localVoicePeer.LastSeen = DateTimeOffset.Now;
            peers.Insert(0, _localVoicePeer);
        }
        else
        {
            _localVoicePeer = null;
        }
        var signature = string.Join("|", peers.Select(peer => $"{ResolveVoicePeerId(peer)}@{peer.NetworkAddress}"));
        var shouldRefreshList =
            !string.Equals(signature, _lastVoicePeerListSignature, StringComparison.Ordinal) ||
            _voicePeers.Count != peers.Count ||
            !_voicePeers.Zip(peers).All(pair => ReferenceEquals(pair.First, pair.Second));

        if (shouldRefreshList)
        {
            _lastVoicePeerListSignature = signature;
            _voicePeers.Clear();
            foreach (var peer in peers)
            {
                _voicePeers.Add(peer);
            }
        }

        UpdateVoicePeersFromDiscovery();
        RefreshVoiceSettingsWindow();
    }

    private void UpdateVoicePeersFromDiscovery()
    {
        if (_voiceChannel is null)
        {
            return;
        }

        if (!_voiceChannel.IsJoined)
        {
            if (_lastVoiceTransportPeerSignature.Length > 0)
            {
                _lastVoiceTransportPeerSignature = "";
                _voiceChannel.UpdatePeers(Array.Empty<VoicePeerCandidate>());
            }
            return;
        }

        var peers = _voicePresence
            .Where(pair => pair.Value.LastSeenUtc >= DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30))
            .SelectMany(pair => pair.Value.Endpoints.Select(endpoint =>
                new VoicePeerCandidate(pair.Key, endpoint.Address, endpoint.ProviderId)))
            .Where(peer => !string.IsNullOrWhiteSpace(peer.PeerId))
            .Distinct()
            .ToArray();
        var signature = string.Join("|", peers.Select(peer =>
            $"{peer.PeerId}@{peer.Address}:{peer.ProviderId}"));
        if (string.Equals(signature, _lastVoiceTransportPeerSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastVoiceTransportPeerSignature = signature;
        _voiceChannel.UpdatePeers(peers);
    }

    private static string ResolveVoicePeerId(PeerViewModel? peer)
    {
        if (peer is null)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(peer.IdentityId))
        {
            return peer.IdentityId;
        }

        return peer.NetworkAddress;
    }

    private WorldMetadataContext? CreateWorldMetadataContext()
    {
        var owner = GetActiveLocalOwner();
        if (BuildComboBox.SelectedItem is not ClientBuildViewModel build)
        {
            return null;
        }

        return new WorldMetadataContext
        {
            BuildName = build.Name,
            BuildRelativePath = build.RelativePath,
            PackHash = _localPackHash,
            OwnerIdentityId = owner.id,
            OwnerIdentityName = owner.name
        };
    }

    private WorldViewModel CreateWorldViewModel(string path, WorldMetadataContext? metadataContext)
    {
        var metadata = RequireWorldMetadata().EnsureMetadata(path, metadataContext);
        if (metadataContext is not null)
        {
            _ = RequireWorldMetadata().TryWriteOwnerMetadata(
                path,
                metadataContext.OwnerIdentityId,
                metadataContext.OwnerIdentityName,
                overwriteExistingOwner: false);
            _ = RequireWorldMetadata().TryWriteCurrentHolderMetadata(
                path,
                metadataContext.OwnerIdentityId,
                metadataContext.OwnerIdentityName,
                transferred: false);
        }

        var buildName = string.IsNullOrWhiteSpace(metadata?.BuildName)
            ? RequireWorldMetadata().GetBuildName(path)
            : metadata.BuildName;

        return new WorldViewModel
        {
            Name = Path.GetFileName(path),
            Path = path,
            BuildName = buildName
        };
    }

    private async Task StartNetworkingAsync()
    {
        var settings = RequireSettings();
        await RequireTransfer().StartListenerAsync(settings, _lifetimeCts.Token);
        if (_networkEndpoints.Count == 0)
        {
            await RequireDiscovery().StopAsync();
            RequireLogger().Warn("No usable network endpoint is available.");
            return;
        }

        await RequireDiscovery().StartAsync(_networkSnapshot, CreateAnnouncement);
    }

    private async Task RefreshPackHashAndNetworkingAsync()
    {
        try
        {
            await RefreshPackHashAsync();
            if (_startupComplete) await StartNetworkingAsync();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Pack refresh failed: {ex.Message}");
        }
    }

    private PeerAnnouncement CreateAnnouncement(NetworkEndpointInfo endpoint)
    {
        var settings = RequireSettings();
        var identity = ResolveActiveLocalIdentity();
        RefreshOpenToLanState();
        var openToLanPort = _openToLanPort;
        var identityContext = RequireIdentityService().ResolveContext(settings);
        RequireWaypointSync().UpdateHostingState(
            openToLanPort.HasValue,
            settings.ClientRelativePath,
            _localPackHash,
            identityContext);
        var waypointHost = RequireWaypointSync().GetHostAdvertisement();
        var skin = RequireSkinService().GetAnnouncement(settings, identity.id);
        var advertisedEndpoints = _networkEndpoints
            .Where(candidate =>
                string.Equals(candidate.ProviderId, endpoint.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.InterfaceId, endpoint.InterfaceId, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new PeerAdvertisedEndpoint
            {
                Address = candidate.NetworkAddress,
                ProviderId = candidate.ProviderId,
                InterfaceId = candidate.InterfaceId,
                AddressFamily = candidate.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                NetworkType = VirtualNetworkService.DetectNetworkType(candidate)
            })
            .ToList();
        return new PeerAnnouncement
        {
            ProtocolVersion = PeerDiscoveryService.ProtocolVersion,
            PlayerName = identity.name,
            IdentityId = identity.id,
            IdentityName = identity.name,
            NetworkAddress = endpoint.NetworkAddress,
            NetworkProviderId = endpoint.ProviderId,
            NetworkInterfaceId = endpoint.InterfaceId,
            NetworkAddressFamily = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
            NetworkType = VirtualNetworkService.DetectNetworkType(endpoint),
            NetworkEndpoints = advertisedEndpoints,
            IsHost = openToLanPort.HasValue,
            PackHash = _localPackHash,
            ServerPort = openToLanPort ?? 0,
            State = openToLanPort.HasValue ? "LAN open" : "Minecraft",
            IsVoiceChannelActive = _voiceChannel?.IsJoined == true,
            IsVoiceMuted = _voiceChannel?.IsMuted == true,
            IsMinecraftRunning = _minecraftRunning,
            IsMinecraftPreparing = _minecraftPreparing,
            IsSkinAvailable = skin.IsAvailable,
            SkinSha256 = skin.Sha256,
            SkinModel = skin.Model,
            HostedWorldId = waypointHost?.WorldId ?? string.Empty,
            WaypointProtocolVersion = WaypointSyncService.ProtocolVersion,
            WaypointProviders = waypointHost?.Providers.ToList() ?? []
        };
    }

    private void OnVoiceSpeakingStateChanged(string peerId, bool isSpeaking)
    {
        Dispatcher.Invoke(() =>
        {
            var peer = _voicePeers.FirstOrDefault(item =>
                string.Equals(ResolveVoicePeerId(item), peerId, StringComparison.OrdinalIgnoreCase));
            if (peer is null)
            {
                return;
            }

            peer.IsSpeaking = isSpeaking;
            RefreshVoiceSettingsWindow();
        });
    }

    private void OnVoicePeerPresenceChanged(string peerId, bool isPresent, bool explicitLeave)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (isPresent)
            {
                if (_voicePresence.TryGetValue(peerId, out var existing))
                {
                    existing.LastSeenUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    var peer = _peers.FirstOrDefault(candidate =>
                        string.Equals(ResolveVoicePeerId(candidate), peerId, StringComparison.OrdinalIgnoreCase));
                    if (peer is not null)
                    {
                        _voicePresence[peerId] = new VoicePresenceEntry(
                            peer,
                            peer.GetCandidateEndpoints(preferredProviderId: _primaryEndpoint?.ProviderId).ToArray(),
                            DateTimeOffset.UtcNow);
                    }
                }
            }
            else if (explicitLeave ||
                     (_voicePresence.TryGetValue(peerId, out var existing) &&
                      DateTimeOffset.UtcNow - existing.LastSeenUtc >= TimeSpan.FromSeconds(30)))
            {
                _voicePresence.Remove(peerId);
            }
            RefreshVoicePeers();
        });
    }

    private void OnVoiceTrafficProtectionChanged(bool enabled)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(RefreshUi);
    }

    internal double GetCurrentUiScale()
    {
        try
        {
            if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
            {
                return 1d;
            }

            var bounds = RootGrid.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight));
            var scaleX = bounds.Width / RootGrid.ActualWidth;
            var scaleY = bounds.Height / RootGrid.ActualHeight;
            var scale = Math.Min(scaleX, scaleY);
            return double.IsFinite(scale) && scale > 0 ? scale : 1d;
        }
        catch
        {
            return 1d;
        }
    }

    private void ApplyPeer(PeerAnnouncement announcement)
    {
        var localIdentity = ResolveActiveLocalIdentity();
        if (!string.IsNullOrWhiteSpace(announcement.IdentityId) &&
            string.Equals(announcement.IdentityId, localIdentity.id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (announcement.NetworkAddress is not null && RequireDiscovery().IsLocalAddress(announcement.NetworkAddress))
        {
            return;
        }

        RequireWaypointSync().ObservePeer(announcement);

        var peer = !string.IsNullOrWhiteSpace(announcement.IdentityId)
            ? _peers.FirstOrDefault(item =>
                string.Equals(item.IdentityId, announcement.IdentityId, StringComparison.OrdinalIgnoreCase))
            : _peers.FirstOrDefault(item =>
                item.NetworkEndpoints.Any(endpoint =>
                    string.Equals(endpoint.Address, announcement.NetworkAddress, StringComparison.OrdinalIgnoreCase)));
        if (peer is null)
        {
            peer = new PeerViewModel();
            _peers.Add(peer);
        }

        peer.SetPreferredProvider(_primaryEndpoint?.ProviderId);
        peer.Apply(announcement, _localPackHash);
        RequireSkinService().ObservePeer(peer);
        var voicePeerId = ResolveVoicePeerId(peer);
        if (!string.IsNullOrWhiteSpace(voicePeerId))
        {
            if (announcement.IsVoiceChannelActive)
            {
                _voicePresence[voicePeerId] = new VoicePresenceEntry(
                    peer,
                    peer.GetCandidateEndpoints(preferredProviderId: _primaryEndpoint?.ProviderId).ToArray(),
                    DateTimeOffset.UtcNow);
            }
            else
            {
                _voicePresence.Remove(voicePeerId);
            }
        }
        if (OnlinePlayerComboBox.SelectedItem is null)
        {
            OnlinePlayerComboBox.SelectedItem = peer;
        }
        RefreshHostPeers();
        RefreshVoicePeers();
        UpdateVoicePeersFromDiscovery();
        RefreshLanAdvertisementState();
        RefreshUi();
    }

    private void PruneStalePeers()
    {
        var cutoff = DateTimeOffset.Now - PeerTtl;
        var selectedIp = (OnlinePlayerComboBox.SelectedItem as PeerViewModel)?.NetworkAddress;
        for (var index = _peers.Count - 1; index >= 0; index--)
        {
            if (!_peers[index].PruneEndpoints(cutoff))
            {
                _peers.RemoveAt(index);
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedIp))
        {
            OnlinePlayerComboBox.SelectedItem = _peers.FirstOrDefault(peer =>
                string.Equals(peer.NetworkAddress, selectedIp, StringComparison.OrdinalIgnoreCase));
        }

        if (OnlinePlayerComboBox.SelectedItem is null && _peers.Count > 0)
        {
            OnlinePlayerComboBox.SelectedItem = _peers[0];
        }

        RefreshVoicePeers();
        UpdateVoicePeersFromDiscovery();
        RefreshHostPeers();
        RefreshLanAdvertisementState();
    }

    private void RefreshLanAdvertisementState()
    {
        if (_lanAdvertisement is null || _settings is null) return;
        var identity = ResolveActiveLocalIdentity();
        _lanAdvertisement.Update(
            _openToLanPort,
            string.IsNullOrWhiteSpace(identity.name) ? "Minecraft LAN" : identity.name,
            _networkEndpoints,
            _peers);
    }

    private void RefreshHostLatencies()
    {
        if (_hostRttScanInProgress) return;

        var hostPeers = _peers
            .Where(peer => peer.IsHost && peer.ServerPort > 0)
            .Where(peer => !string.IsNullOrWhiteSpace(peer.NetworkAddress))
            .ToList();
        if (hostPeers.Count == 0) return;

        _hostRttScanInProgress = true;
        _ = UpdateHostLatenciesAsync(hostPeers);
    }

    private async Task UpdateHostLatenciesAsync(IReadOnlyList<PeerViewModel> hostPeers)
    {
        try
        {
            var probes = hostPeers.Select(async peer =>
            {
                var peerIp = peer.NetworkAddress;
                var peerPort = Math.Clamp(peer.ServerPort, 1, 65535);
                var result = await CheckHostReachabilityAsync(peerIp, peerPort, attempts: 1, timeout: TimeSpan.FromMilliseconds(600));

                await Dispatcher.InvokeAsync(() =>
                {
                    var current = _peers.FirstOrDefault(item =>
                        item.IsHost &&
                        item.ServerPort == peer.ServerPort &&
                        string.Equals(item.NetworkAddress, peerIp, StringComparison.OrdinalIgnoreCase));
                    if (current is null) return;

                    current.LastRttMs = result.IsReachable ? result.RoundTripMs : null;
                    if (result.IsReachable)
                    {
                        current.LastRttAt = DateTimeOffset.Now;
                    }
                    else if (current.LastRttAt == default)
                    {
                        current.LastRttAt = DateTimeOffset.UtcNow;
                    }
                });
            });

            await Task.WhenAll(probes);
            await Dispatcher.InvokeAsync(RefreshHostPeers);
        }
        finally
        {
            _hostRttScanInProgress = false;
        }
    }

    private void RefreshOpenToLanState()
    {
        var port = TryDetectOpenToLanPort();

        if (_openToLanPort == port) return;

        _openToLanPort = port;
        if (port.HasValue)
        {
            RequireLogger().Info($"Minecraft Open to LAN detected on port {port.Value}.");
            SetState($"LAN open: {port.Value}");
        }
        else if (_state.StartsWith("LAN open:", StringComparison.OrdinalIgnoreCase))
        {
            SetState("Minecraft");
            RequireLogger().Info("Minecraft Open to LAN is no longer detected.");
        }
    }

    private int? TryDetectOpenToLanPort()
    {
        if (_paths is null || _settings is null) return null;

        var logPath = Path.Combine(_paths.CombineUnderInstances(_settings.ClientRelativePath), "logs", "latest.log");
        if (!File.Exists(logPath)) return null;

        var cachedPort = _cachedOpenToLanPort;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _openToLanLogPosition)
            {
                _openToLanLogPosition = 0;
            }

            stream.Seek(_openToLanLogPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is null) continue;
                if (TryParseOpenToLanPort(line, out var parsedPort))
                {
                    cachedPort = parsedPort;
                    continue;
                }

                if (cachedPort is not null && LanClosedRegex().IsMatch(line))
                {
                    cachedPort = null;
                }
            }

            _openToLanLogPosition = stream.Position;
        }
        catch
        {
            return _openToLanPort;
        }

        if (cachedPort is null)
        {
            _cachedOpenToLanPort = null;
            _openToLanPort = null;
            return null;
        }

        var activePort = cachedPort.Value;
        if (!IsTcpPortListening(activePort))
        {
            RequireLogger().Info($"Detected stale LAN port {activePort}; no active TCP listener found.");
            _cachedOpenToLanPort = null;
            _openToLanPort = null;
            return null;
        }

        if (_cachedOpenToLanPort != cachedPort)
        {
            _cachedOpenToLanPort = activePort;
            RequireLogger().Info($"Minecraft Open to LAN detected on port {activePort}.");
        }
        return activePort;
    }

    private static bool TryParseOpenToLanPort(string line, out int port)
    {
        port = 0;
        if (!line.Contains("Started serving on", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var index = line.IndexOf("Started serving on", StringComparison.OrdinalIgnoreCase);
        var tail = line[(index + "Started serving on".Length)..];
        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        var matches = OpenToLanPortNumberRegex().Matches(tail);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (!int.TryParse(matches[i].Value, out var candidate))
            {
                continue;
            }

            if (candidate is > 0 and <= 65535)
            {
                port = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsTcpPortListening(int port)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        return listeners.Any(listener => listener.Port == port);
    }

    [GeneratedRegex("\\b\\d{2,5}\\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpenToLanPortNumberRegex();

    [GeneratedRegex("Stopping (?:singleplayer )?server|Saving and pausing game|disconnect", RegexOptions.IgnoreCase)]
    private static partial Regex LanClosedRegex();

    private void OnMinecraftClientRunningChanged(bool isRunning)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _minecraftRunning = isRunning;
            if (!isRunning)
            {
                RefreshWorlds();
                RefreshOpenToLanState();
            }
            RefreshUi();
        });
    }

    private void OnMinecraftClientPreparingChanged(bool isPreparing)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            _minecraftPreparing = isPreparing;
            RefreshUi();
        });
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minecraftPreparing || _minecraftRunning) return;
        _minecraftPreparing = true;
        RefreshUi();
        try
        {
            if (RequireTransfer().IsOperationActive)
            {
                throw new InvalidOperationException("Wait for the world transfer to finish before starting Minecraft.");
            }
            ApplyPlayerName();
            ApplyMemoryText();
            var settings = RequireSettings();
            if (BuildComboBox.SelectedItem is not ClientBuildViewModel build)
            {
                throw new InvalidOperationException($"No Minecraft pack with {PackManifestService.ManifestFileName} was found in ./Minecraft/Packs.");
            }

            if (!string.Equals(settings.ClientRelativePath, build.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                settings.ClientRelativePath = build.RelativePath;
                RequireSettingsService().Save(settings);
            }

            await RefreshPackHashAsync();
            if (_localPackHash == "missing")
            {
                throw new InvalidOperationException($"Pack validation failed: ./Minecraft/Packs/{settings.ClientRelativePath} is missing.");
            }

            RequireSettingsService().Save(settings);

            SetState("Starting client");
            var runtimeProgress = new Progress<RuntimePreparationProgress>(ApplyRuntimeProgress);
            try
            {
                await RequireMinecraft().StartClientAsync(settings, null, 0, runtimeProgress, _lifetimeCts.Token);
            }
            catch
            {
                ApplyRuntimeProgress(new RuntimePreparationProgress(
                    RuntimePreparationStage.Failed,
                    "Не удалось подготовить сборку"));
                throw;
            }
            SetState("Minecraft");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _minecraftPreparing = _minecraft?.IsClientPreparing == true;
            RefreshUi();
        }
    }

    private void SkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minecraftRunning || _settings is null || _skinService is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите скин Minecraft",
            Filter = "PNG (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var identity = ResolveActiveLocalIdentity();
            var skin = _skinService.SelectLocalSkin(_settings, identity.id, dialog.FileName);
            RequireSettingsService().Save(_settings);
            SetState($"Skin selected ({skin.Model})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            RequireLogger().Warn($"Skin selection failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<(string Ip, int Port)?> GetSelectedHostAddressAsync(PeerViewModel? peer)
    {
        if (peer is null || peer.ServerPort <= 0)
        {
            if (peer is null)
            {
                SetState("No host selected.");
            }
            else
            {
                SetState($"Host {peer.PlayerName} has no open LAN port.");
            }
            return null;
        }

        if (!peer.IsHost)
        {
            SetState($"Player {peer.PlayerName} is not marked as host.");
            return null;
        }

        if (!peer.State.Contains("LAN open", StringComparison.OrdinalIgnoreCase))
        {
            SetState($"Player {peer.PlayerName} is not advertising an open LAN session.");
            return null;
        }

        if (!IPAddress.TryParse(peer.NetworkAddress, out var peerIp))
        {
            SetState($"Invalid host IP: {peer.NetworkAddress}");
            return null;
        }

        if (IPAddress.IsLoopback(peerIp))
        {
            SetState("Selected host resolves to local loopback.");
            return null;
        }

        var host = (Ip: peer.NetworkAddress, Port: Math.Clamp(peer.ServerPort, 1, 65535));
        var reachability = await CheckHostReachabilityAsync(host.Ip, host.Port, HostReachabilityAttempts, HostReachabilityTimeout);
        if (!reachability.IsReachable)
        {
            SetState($"Host {host.Ip}:{host.Port} is not reachable. {reachability.FailureReason}");
            return null;
        }

        peer.LastRttMs = reachability.RoundTripMs;
        peer.LastRttAt = DateTimeOffset.Now;
        return host;
    }

    private static async Task<HostReachabilityResult> CheckHostReachabilityAsync(string host, int port, int attempts, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
        {
            return HostReachabilityResult.Failed("Invalid host endpoint.");
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var cts = new CancellationTokenSource(timeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!IPAddress.TryParse(host, out var address))
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }
                using var tcpClient = new TcpClient(address.AddressFamily);
                await tcpClient.ConnectAsync(address, port, cts.Token);
                return HostReachabilityResult.Succeeded(stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(300, 120 * attempt)), CancellationToken.None);
                }
            }
        }

        var reason = lastError?.Message ?? "Connection attempt failed.";
        if (reason.Length > 140)
        {
            reason = reason[..140];
        }

        return HostReachabilityResult.Failed(reason);
    }

    private sealed class HostReachabilityResult
    {
        public bool IsReachable { get; }
        public int? RoundTripMs { get; }
        public string FailureReason { get; }

        private HostReachabilityResult(bool isReachable, int? roundTripMs, string failureReason)
        {
            IsReachable = isReachable;
            RoundTripMs = roundTripMs;
            FailureReason = failureReason;
        }

        public static HostReachabilityResult Succeeded(long roundTripMs)
            => new(true, (int)Math.Clamp(roundTripMs, 0, int.MaxValue), string.Empty);

        public static HostReachabilityResult Failed(string reason)
            => new(false, null, reason);
    }

    private async void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transferActive || _minecraftRunning || _minecraftPreparing)
        {
            return;
        }

        try
        {
            if (WorldComboBox.SelectedItem is not WorldViewModel world)
            {
                throw new InvalidOperationException("Choose a world to transfer.");
            }

            if (OnlinePlayerComboBox.SelectedItem is not PeerViewModel peer)
            {
                throw new InvalidOperationException("Choose an online player.");
            }
            if (peer.IsMinecraftRunning || peer.IsMinecraftPreparing)
            {
                throw new InvalidOperationException("The selected player is currently in Minecraft or preparing it.");
            }

            var settings = RequireSettings();
            SetState("Transferring world");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            await RequireTransfer().SendWorldAsync(peer, settings, world.Path, cts.Token);
            RequireSettingsService().Save(settings);
            RefreshWorlds();
            SetState("World sent");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshUi();
        }
    }

    private void PlayerNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextPersistence || !_isEditingPlayerName) return;
        RefreshUi();
    }

    private void PlayerNameTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            e.Handled = true;
            return;
        }

        var draft = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, e.Text);
        e.Handled = !LocalIdentityService.IsNicknameDraftValid(draft);
    }

    private void PlayerNameTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox ||
            !e.DataObject.GetDataPresent(DataFormats.Text) ||
            e.DataObject.GetData(DataFormats.Text) is not string text)
        {
            e.CancelCommand();
            return;
        }

        var draft = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, text);
        if (!LocalIdentityService.IsNicknameDraftValid(draft))
        {
            e.CancelCommand();
        }
    }

    private void PlayerNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
    }

    private async void PlayerNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isEditingPlayerName) return;
        if (e.Key == Key.Escape)
        {
            CancelPlayerNameEdit();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        await SavePlayerNameAsync();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private async void ChangePlayerNameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_isEditingPlayerName)
        {
            _isEditingPlayerName = true;
            PlayerNameTextBox.IsReadOnly = false;
            ChangePlayerNameButton.Content = "Сохранить";
            PlayerNameTextBox.Focus();
            PlayerNameTextBox.SelectAll();
            RefreshUi();
            return;
        }

        await SavePlayerNameAsync();
    }

    private async Task SavePlayerNameAsync()
    {
        var candidate = PlayerNameTextBox.Text;
        await RunUiActionAsync(() =>
        {
            var settings = RequireSettings();
            if (!LocalIdentityService.TryNormalizeNickname(candidate, out var normalized, out var error))
            {
                throw new InvalidOperationException(error);
            }
            var previousName = LocalIdentityService.NormalizeNickname(settings.PlayerName, Environment.UserName);
            if (string.Equals(previousName, normalized, StringComparison.Ordinal))
            {
                FinishPlayerNameEdit(normalized);
                return Task.CompletedTask;
            }
            if (_minecraft?.IsClientRunning == true)
            {
                throw new InvalidOperationException("Close Minecraft before changing the nickname.");
            }
            settings.PreviousPlayerName = string.Equals(previousName, normalized, StringComparison.Ordinal)
                ? settings.PreviousPlayerName
                : previousName;
            settings.PlayerName = normalized;
            PersistActivePlayerIdentity();
            FinishPlayerNameEdit(normalized);
            return Task.CompletedTask;
        });
    }

    private void CancelPlayerNameEdit()
    {
        FinishPlayerNameEdit(RequireSettings().PlayerName);
    }

    private void FinishPlayerNameEdit(string name)
    {
        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = name;
            PlayerNameTextBox.CaretIndex = name.Length;
        }
        finally
        {
            _suppressTextPersistence = false;
        }

        _isEditingPlayerName = false;
        PlayerNameTextBox.IsReadOnly = true;
        ChangePlayerNameButton.Content = "Изменить";
        RefreshUi();
    }

    private void ResolveAndPersistLocalIdentity()
    {
        if (_identityService is null || _settings is null || _settingsService is null) return;

        _settings.PlayerName = LocalIdentityService.NormalizeNickname(_settings.PlayerName, Environment.UserName);
        var resolvedContext = _identityService.ResolveContext(_settings);
        var updated = false;

        var identityId = resolvedContext.IdentityId ?? "";
        if (!string.Equals(_settings.LocalIdentityId, identityId, StringComparison.Ordinal))
        {
            _settings.LocalIdentityId = identityId;
            updated = true;
        }

        var identityName = resolvedContext.IdentityName ?? "";
        if (!string.Equals(_settings.LocalIdentityName, identityName, StringComparison.Ordinal))
        {
            _settings.LocalIdentityName = identityName;
            updated = true;
        }

        if (updated || !string.IsNullOrWhiteSpace(_settings.PlayerName))
        {
            _settingsService.Save(_settings);
        }
    }

    private void RefreshPlayerIdentityDisplay()
    {
        if (_isEditingPlayerName || PlayerNameTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        var displayName = RequireSettings().PlayerName;
        if (string.Equals(PlayerNameTextBox.Text, displayName, StringComparison.Ordinal))
        {
            return;
        }

        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = displayName;
        }
        finally
        {
            _suppressTextPersistence = false;
        }
    }

    private string GetPlayerDisplayName()
    {
        var playerName = RequireSettings().PlayerName;
        return string.IsNullOrWhiteSpace(playerName)
            ? LocalIdentityService.NormalizeNickname(null, Environment.UserName)
            : playerName.Trim();
    }

    private void ApplyPlayerName()
    {
        if (_settings is null || _identityService is null || _settingsService is null)
        {
            return;
        }

        var normalized = LocalIdentityService.NormalizeNickname(PlayerNameTextBox.Text, Environment.UserName);

        _suppressTextPersistence = true;
        try
        {
            PlayerNameTextBox.Text = normalized;
            PlayerNameTextBox.CaretIndex = normalized.Length;
        }
        finally
        {
            _suppressTextPersistence = false;
        }

        _settings.PlayerName = normalized;
        PersistActivePlayerIdentity();
    }

    private void PersistActivePlayerIdentity()
    {
        if (_settings is null || _identityService is null || _settingsService is null ||
            string.IsNullOrWhiteSpace(_settings.PlayerName))
        {
            return;
        }

        var identity = _identityService.ResolveContext(_settings);
        _settings.LocalIdentityId = identity.IdentityId;
        _settings.LocalIdentityName = identity.IdentityName;
        _settingsService.Save(_settings);
    }

    private (string id, string name) GetActiveLocalOwner()
    {
        var identity = ResolveActiveLocalIdentity();
        return (identity.id, identity.name);
    }

    private (string id, string name) ResolveActiveLocalIdentity()
    {
        var settings = RequireSettings();
        if (_identityService is null)
        {
            return (
                string.IsNullOrWhiteSpace(settings.LocalIdentityId) ? string.Empty : settings.LocalIdentityId.Trim(),
                string.IsNullOrWhiteSpace(settings.LocalIdentityName)
                    ? Environment.UserName
                    : settings.LocalIdentityName.Trim()
            );
        }

        var resolved = _identityService.ResolveContext(settings);
        return (resolved.IdentityId ?? "", resolved.IdentityName ?? "");
    }

    private void MemoryTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyMemoryText();
    }

    private void MemoryTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(ch => !char.IsDigit(ch));
    }

    private void MemoryTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(text) || text.Any(ch => !char.IsDigit(ch)))
        {
            e.CancelCommand();
        }
    }

    private void MemoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressMemoryTextChanged || _settings is null || _settingsService is null) return;

        var digitsOnly = new string(MemoryTextBox.Text.Where(char.IsDigit).ToArray());
        if (!string.Equals(MemoryTextBox.Text, digitsOnly, StringComparison.Ordinal))
        {
            SetMemoryText(digitsOnly);
            return;
        }

        if (string.IsNullOrWhiteSpace(digitsOnly))
        {
            return;
        }

        var maxMemoryGb = GetAllowedMaxMemoryGb();
        if (!int.TryParse(digitsOnly, out var memoryGb) || memoryGb > maxMemoryGb)
        {
            SetMemoryGb(maxMemoryGb);
            return;
        }

        if (memoryGb >= MinMemoryGb)
        {
            _settings.MaxMemoryGb = memoryGb;
            _settingsService.Save(_settings);
        }
    }

    private void MemoryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ApplyMemoryText();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private async void InstallNetworkToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_networkToolInstallBusy) return;
        _networkToolInstallBusy = true;
        RefreshUi();
        try
        {
            var setup = RequireNetworkToolSetup();
            if (setup.GetInstallInfo().IsInstalled)
            {
                RefreshNetworkToolSetupStatus();
                SetState($"{setup.DisplayName} installed");
                return;
            }

            var progress = new Progress<string>(message =>
            {
                SetState(message);
                RequireLogger().Info(message);
            });
            var installer = await setup.DownloadInstallerAsync(progress, _lifetimeCts.Token);
            await setup.InstallAndCleanupAsync(installer, progress, _lifetimeCts.Token);
            RefreshNetworkToolSetupStatus();
            await RefreshNetworkAdaptersAsync(forceRestart: true);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _networkToolInstallBusy = false;
            RefreshUi();
        }
    }

    private async void GenerateNetworkCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() =>
        {
            var credentials = GenerateAndFillNetworkCredentials();
            RequireLogger().Info($"Generated network name: {credentials.NetworkName}");
            SetState("Network credentials generated");
            return Task.CompletedTask;
        });
    }

    private void CopyNetworkNameButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextIfPossible(RequireSettings().NetworkName);
        SetState("Network name copied");
    }

    private void CopyNetworkPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        CopyTextIfPossible(RequireSettings().NetworkPassword);
        SetState("Network password copied");
    }

    private void NetworkToolAutoLaunchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressTextPersistence || _settings is null || _settingsService is null)
        {
            return;
        }

        _settings.NetworkToolAutoLaunch = NetworkToolAutoLaunchCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private void TransferSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshUi();
    }

    private async void NetworkProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNetworkProviderSelection ||
            NetworkProviderComboBox.SelectedItem is not NetworkProviderOption provider ||
            !RequireNetwork().SelectProvider(provider.Id))
        {
            return;
        }

        RequireLogger().Info($"Network provider selected for this session: {provider.DisplayName}.");
        await RefreshNetworkAdaptersSafelyAsync(forceRestart: true);
    }

    private async void BuildComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBuildPersistence || _settings is null || _settingsService is null) return;
        if (BuildComboBox.SelectedItem is not ClientBuildViewModel build) return;

        _settings.ClientRelativePath = build.RelativePath;
        _settingsService.Save(_settings);
        InitializeRuntimeProgressUi();
        await RefreshPackHashAsync();
        if (_startupComplete) await StartNetworkingAsync();
        SetState($"Build selected: {build.Name}");
        RefreshUi();
    }

    private void HostComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshUi();
    }

    private void WorldComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_paths is not null && _settings is not null && _settingsService is not null &&
            WorldComboBox.SelectedItem is WorldViewModel world)
        {
            _settings.SelectedWorldRelativePath = Path.GetRelativePath(_paths.Worlds, world.Path);
            _settingsService.Save(_settings);
        }

        SetTransferProgressVisible(_transferBytesCurrent, _transferBytesTotal);
        RefreshUi();
    }

    private void ApplyTransferProgress(WorldTransferProgress progress)
    {
        var activeChanged = _transferActive != progress.IsActive;
        _transferActive = progress.IsActive;
        SetProgressActivity(TransferProgressBar, progress.IsActive);
        if (!progress.IsActive)
        {
            _transferRate.Reset();
            _transferBytesCurrent = 0;
            _transferBytesTotal = 0;
            _lastTransferSpeedBytesPerSecond = 0;
            SetTransferProgressVisible(0, 0);
            if (activeChanged) RefreshUi();
            return;
        }

        var current = progress.Current;
        var total = progress.Total;
        _lastTransferSpeedBytesPerSecond = _transferRate.Update(current, "world");
        _transferBytesCurrent = Math.Max(0, current);
        _transferBytesTotal = total;
        SetTransferProgressVisible(_transferBytesCurrent, _transferBytesTotal);
        if (activeChanged) RefreshUi();
    }

    private void SetTransferProgressVisible(long current, long total)
    {
        if (total <= 0)
        {
            TransferProgressBar.Value = 0;
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressText.Text = _transferActive ? "Передача..." : "В ожидании мира";
            if (!_transferActive)
            {
                _lastTransferSpeedBytesPerSecond = 0;
            }
            return;
        }

        var clampedTotal = Math.Max(1, total);
        var value = Math.Clamp(current, 0, clampedTotal);
        var percent = Math.Round(value * 100d / clampedTotal, 1);
        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Value = percent;
        TransferProgressText.Text = $"{FormatBytes(value)} / {FormatBytes(clampedTotal)} ({FormatBytes((long)_lastTransferSpeedBytesPerSecond)}/с)";
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        if (bytes >= gb) return $"{bytes / (double)gb:0.##} ГБ";
        if (bytes >= mb) return $"{bytes / (double)mb:0.##} МБ";
        if (bytes >= kb) return $"{bytes / (double)kb:0.##} КБ";
        return $"{bytes} Б";
    }

    private void StartSecretLoading()
    {
        SecretProgressBar.Maximum = 100;
        SecretProgressBar.Value = 0;
        SetProgressActivity(SecretProgressBar, active: false);
        SecretExtendedProgressBar.Maximum = SecretLoadingDuration.TotalSeconds;
        SecretExtendedProgressBar.Value = 0;
        SecretExtendedProgressBar.Visibility = Visibility.Visible;
        SecretProgressText.Text = "Загрузка";
        SecretOpenButton.IsEnabled = false;
        _secretLoadingStartedAt = DateTimeOffset.Now;
        _secretTimer.Start();
        RefreshSecretLoadingProgress();
    }

    private void RefreshSecretLoadingProgress()
    {
        if (_secretLoadingStartedAt is null) return;

        var elapsedSeconds = (DateTimeOffset.Now - _secretLoadingStartedAt.Value).TotalSeconds;
        SecretExtendedProgressBar.Value = Math.Clamp(elapsedSeconds, 0, SecretExtendedProgressBar.Maximum);
        if (SecretExtendedProgressBar.Value >= SecretExtendedProgressBar.Maximum)
        {
            _secretTimer.Stop();
            SecretExtendedProgressBar.Visibility = Visibility.Collapsed;
            SecretExtendedProgressBar.Value = 0;
            SecretProgressBar.Value = SecretProgressBar.Maximum;
            SetProgressActivity(SecretProgressBar, active: false);
            SecretProgressText.Text = "Загрузка завершена";
        }
        else
        {
            SecretProgressText.Text = "Загрузка";
        }

        RefreshUi();
    }

    private void SecretOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (SecretExtendedProgressBar.Visibility == Visibility.Visible ||
            SecretProgressBar.Value < SecretProgressBar.Maximum) return;

        var window = new SecretMessageWindow(GetCurrentUiScale()) { Owner = this };
        window.ShowDialog();
    }

    private static string BuildVersionText()
    {
        return $"Версия {UpdateService.CurrentReleaseNumber}";
    }

    private void InitializeUpdateUi()
    {
        _updateRate.Reset();
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.IsIndeterminate = false;
        SetProgressActivity(UpdateProgressBar, active: false);
        UpdateProgressText.Text = "Вы на последней версии";
        UpdateButton.IsEnabled = false;
    }

    private void InitializeRuntimeProgressUi()
    {
        _runtimeRate.Reset();
        RuntimeProgressBar.Value = 0;
        RuntimeProgressBar.IsIndeterminate = false;
        SetProgressActivity(RuntimeProgressBar, active: false);
        RuntimeProgressText.Text = "В ожидании игры";
    }

    private void ApplyRuntimeProgress(RuntimePreparationProgress progress)
    {
        SetProgressActivity(
            RuntimeProgressBar,
            progress.Stage is RuntimePreparationStage.Checking or
                RuntimePreparationStage.Downloading or
                RuntimePreparationStage.InstallingLoader or
                RuntimePreparationStage.Verifying);
        var phase = progress.PhaseCount > 1 &&
                    progress.PhaseIndex > 0 &&
                    progress.PhaseIndex <= progress.PhaseCount
            ? $" {progress.PhaseIndex}/{progress.PhaseCount}"
            : string.Empty;
        var runtimeSpeed = progress.Stage == RuntimePreparationStage.Downloading && progress.TotalBytes > 0
            ? _runtimeRate.Update(progress.DownloadedBytes, $"runtime:{progress.PhaseIndex}/{progress.PhaseCount}")
            : 0;
        if (progress.Stage != RuntimePreparationStage.Downloading) _runtimeRate.Reset();
        RuntimeProgressText.Text = progress.Stage switch
        {
            RuntimePreparationStage.Downloading when progress.TotalBytes > 0 =>
                $"Скачивание файлов{phase}: {FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)} ({FormatBytes((long)runtimeSpeed)}/с)",
            RuntimePreparationStage.Downloading => $"Скачивание файлов{phase}",
            RuntimePreparationStage.InstallingLoader => progress.Message + phase,
            _ => progress.Message
        };
        RuntimeProgressBar.IsIndeterminate = progress.Fraction is null &&
                                             progress.Stage is RuntimePreparationStage.Checking or
                                                 RuntimePreparationStage.Downloading or
                                                 RuntimePreparationStage.InstallingLoader or
                                                 RuntimePreparationStage.Verifying;
        if (progress.Fraction is not null)
        {
            RuntimeProgressBar.Value = Math.Clamp(progress.Fraction.Value * 100d, 0d, 100d);
        }
        else if (!RuntimeProgressBar.IsIndeterminate)
        {
            RuntimeProgressBar.Value = progress.Stage == RuntimePreparationStage.Ready ? 100d : 0d;
        }
    }

    private async Task CheckForUpdatesAsync(CancellationToken token)
    {
        if (_updateService is null) return;

        PreparedUpdate? startupPrepared = null;
        try
        {
            startupPrepared = await Task.Run(_updateService.TryGetPreparedUpdate, token);
            token.ThrowIfCancellationRequested();
            if (startupPrepared is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _updateBusy = true;
                    _preparedUpdate = startupPrepared;
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 100;
                    SetProgressActivity(UpdateProgressBar, active: false);
                    UpdateProgressText.Text = "Обновление готово к установке";
                    RefreshUi();
                });
            }

            var result = startupPrepared is null
                ? await _updateService.CheckAsync(token)
                : await _updateService.CheckAsync(
                    token,
                    attempts: 1,
                    attemptTimeout: TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
            if (!result.IsUpdateAvailable)
            {
                if (startupPrepared is null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _preparedUpdate = null;
                        UpdateProgressBar.IsIndeterminate = false;
                        UpdateProgressBar.Value = 0;
                        SetProgressActivity(UpdateProgressBar, active: false);
                        UpdateProgressText.Text = "Вы на последней версии";
                    });
                }
            }
            else if (result.Manifest is not null &&
                     (startupPrepared is null || ShouldReplacePreparedUpdate(startupPrepared.Manifest, result.Manifest)))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _updateBusy = true;
                    _updateRate.Reset();
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 0;
                    SetProgressActivity(UpdateProgressBar, active: true);
                    UpdateProgressText.Text = "Скачивается обновление";
                    RefreshUi();
                });

                var progress = new Progress<UpdatePreparationProgress>(value =>
                {
                    UpdateProgressBar.IsIndeterminate = value.Fraction is null;
                    if (value.Fraction is not null)
                    {
                        UpdateProgressBar.Value = Math.Clamp(value.Fraction.Value * 100d, 0d, 100d);
                    }
                    if (value.Stage == UpdatePreparationStage.Downloading && value.TotalBytes > 0)
                    {
                        var speed = _updateRate.Update(value.DownloadedBytes, "update");
                        UpdateProgressText.Text =
                            $"Скачивается обновление: {FormatBytes(value.DownloadedBytes)} / {FormatBytes(value.TotalBytes)} ({FormatBytes((long)speed)}/с)";
                    }
                    else if (value.Stage == UpdatePreparationStage.ApplyingDelta)
                    {
                        _updateRate.Reset();
                        UpdateProgressText.Text = "Применение обновления";
                    }
                });
                var readyUpdate = await _updateService.DownloadUpdateAsync(result, progress, token);
                token.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() =>
                {
                    _preparedUpdate = readyUpdate;
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 100;
                    SetProgressActivity(UpdateProgressBar, active: false);
                    UpdateProgressText.Text = "Обновление готово к установке";
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Background update failed: {ex.Message}");
            if (startupPrepared is null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _preparedUpdate = null;
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 0;
                    SetProgressActivity(UpdateProgressBar, active: false);
                    UpdateProgressText.Text = "Вы на последней версии";
                });
            }
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _updateBusy = false;
                    RefreshUi();
                });
            }
        }
    }

    private static bool ShouldReplacePreparedUpdate(UpdateManifest cached, UpdateManifest remote)
    {
        if (remote.ReleaseNumber < cached.ReleaseNumber) return false;
        return !string.Equals(remote.CommitSha, cached.CommitSha, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(remote.Sha256, cached.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _preparedUpdate is null) return;

        _updateBusy = true;
        RefreshUi();
        try
        {
            var prepared = await Task.Run(RequireUpdateService().TryGetPreparedUpdate, _lifetimeCts.Token);
            if (prepared is null)
            {
                _preparedUpdate = null;
                UpdateProgressText.Text = "Вы на последней версии";
                UpdateProgressBar.Value = 0;
                SetProgressActivity(UpdateProgressBar, active: false);
                return;
            }

            _preparedUpdate = prepared;
            UpdateProgressText.Text = "Обновление готово к установке";
            UpdateProgressBar.Value = 100;
            SetProgressActivity(UpdateProgressBar, active: false);
            _restartAfterUpdateOnExit = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn($"Update failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateBusy = false;
            RefreshUi();
        }
    }

    private async void VoiceJoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceChannel is null || _settings is null || _settingsService is null) return;

        await RunVoiceActionAsync(async () =>
        {
            if (_voiceChannel.IsJoined)
            {
                _voicePttInputPressed = false;
                _voicePttToggleActive = false;
                _voiceChannel.SetPttPressed(false);
                await _voiceChannel.LeaveAsync();
                RefreshVoicePeers();
                SetState("Voice channel left");
                return;
            }

            EnsureVoiceDevicesSelection();
            _voiceChannel.SetDeviceIds(_settings.VoiceInputDeviceId, _settings.VoiceOutputDeviceId);
            _voiceChannel.Initialize(_settings);
            _voiceChannel.SetInputVolume(_settings.VoiceInputVolume);
            _voiceChannel.SetOutputVolume(_settings.VoiceOutputVolume);
            _voiceChannel.Join();
            _voicePttInputPressed = false;
            _voicePttToggleActive = false;
            RefreshVoicePeers();
            UpdateVoicePeersFromDiscovery();
            ApplyVoiceTransmissionState();
            SetState("Voice channel joined");
            await Task.Yield();
        });
    }

    private void VoiceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceSettingsWindow is { IsVisible: true })
        {
            _voiceSettingsWindow.Activate();
            return;
        }

        _voiceSettingsWindow = new VoiceSettingsWindow(this, GetCurrentUiScale());
        _voiceSettingsWindow.Show();
        RefreshVoiceSettingsWindow(_voiceSettingsWindow);
    }

    private void EnsureVoiceDevicesSelection()
    {
        if (_settings is null || _voiceChannel is null) return;

        var input = string.IsNullOrWhiteSpace(_settings.VoiceInputDeviceId)
            ? VoiceInputComboBox.SelectedItem as VoiceAudioDevice
            : null;
        var output = string.IsNullOrWhiteSpace(_settings.VoiceOutputDeviceId)
            ? VoiceOutputComboBox.SelectedItem as VoiceAudioDevice
            : null;

        if (input is not null)
        {
            _settings.VoiceInputDeviceId = input.Id;
        }

        if (output is not null)
        {
            _settings.VoiceOutputDeviceId = output.Id;
        }

        _voiceChannel.SetDeviceIds(
            _settings.VoiceInputDeviceId,
            _settings.VoiceOutputDeviceId);
    }

    private void VoiceInputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVoicePersistence || _voiceChannel is null || _settings is null || _settingsService is null) return;

        if (VoiceInputComboBox.SelectedItem is VoiceAudioDevice device)
        {
            SetVoiceInputDevice(device);
        }
    }

    private void VoiceOutputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVoicePersistence || _voiceChannel is null || _settings is null || _settingsService is null) return;

        if (VoiceOutputComboBox.SelectedItem is VoiceAudioDevice device)
        {
            SetVoiceOutputDevice(device);
        }
    }

    private void VoiceMasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTextPersistence || _voiceChannel is null || _settings is null || _settingsService is null) return;

        SetVoiceMasterVolume(e.NewValue);
    }

    private void VoiceMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceChannel is null || _settings is null || _settingsService is null) return;

        ToggleVoiceMute();
    }

    private void VoiceProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceChannel is not { IsJoined: true }) return;
        _voiceChannel.ToggleTrafficProtection();
        RefreshUi();
    }

    private void VoiceDeafenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceChannel is null || _settings is null || _settingsService is null) return;

        ToggleVoiceDeafen();
    }

    private void VoicePttButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _voiceChannel?.SetPttPressed(true);
    }

    private void VoicePttButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _voiceChannel?.SetPttPressed(false);
    }

    private void VoicePttButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _voiceChannel?.SetPttPressed(false);
    }

    private void VoicePttButton_MouseLeave(object sender, MouseEventArgs e)
    {
        _voiceChannel?.SetPttPressed(false);
    }

    private void VoicePeerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || slider.DataContext is not PeerViewModel peer || _voiceChannel is null)
        {
            return;
        }

        var peerId = ResolveVoicePeerId(peer);
        if (string.IsNullOrWhiteSpace(peerId))
        {
            return;
        }

        SetVoicePeerVolume(peer, e.NewValue);
    }

    private async Task AutoLaunchInstalledNetworkToolAsync()
    {
        try
        {
            var setup = RequireNetworkToolSetup();
            if (setup.Launch())
            {
                await RequireNetwork().WaitForProviderAsync(
                    setup.ToolId,
                    TimeSpan.FromSeconds(5),
                    _lifetimeCts.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn($"Network tool auto-launch failed: {ex.Message}");
        }
    }

    private NetworkCredentials GenerateAndFillNetworkCredentials()
    {
        var settings = RequireSettings();
        var playerName = GetPlayerDisplayName();
        var credentials = RequireNetworkToolSetup().GenerateCredentials(playerName);
        settings.NetworkName = credentials.NetworkName;
        settings.NetworkPassword = credentials.Password;
        RequireSettingsService().Save(settings);

        _suppressTextPersistence = true;
        try
        {
            RefreshNetworkCredentialsText();
        }
        finally
        {
            _suppressTextPersistence = false;
        }

        return credentials;
    }

    private void RefreshNetworkCredentialsText()
    {
        if (_settings is null)
        {
            return;
        }

        NetworkNameTextBox.Text = _settings.NetworkName?.Trim() ?? string.Empty;
        NetworkPasswordTextBox.Text = _settings.NetworkPassword?.Trim() ?? string.Empty;
    }

    private void RefreshNetworkToolSetupStatus()
    {
        if (_networkToolSetup is null) return;
        var installInfo = _networkToolSetup.GetInstallInfo();
        _networkToolInstalled = installInfo.IsInstalled;
        SetState(installInfo.IsInstalled
            ? $"{_networkToolSetup.DisplayName} installed"
            : $"{_networkToolSetup.DisplayName} not installed");
    }

    private void ApplyMemoryText()
    {
        if (int.TryParse(MemoryTextBox.Text.Trim(), out var memoryGb))
        {
            SetMemoryGb(memoryGb);
            return;
        }

        SetMemoryGb(MinMemoryGb);
    }

    private void SetMemoryGb(int memoryGb)
    {
        var settings = RequireSettings();
        var clamped = ClampMemoryGb(memoryGb);
        settings.MaxMemoryGb = clamped;
        RequireSettingsService().Save(settings);
        SetMemoryText(clamped.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshMemoryText(bool saveIfChanged = false)
    {
        var settings = RequireSettings();
        var clamped = ClampMemoryGb(settings.MaxMemoryGb);
        if (settings.MaxMemoryGb != clamped)
        {
            settings.MaxMemoryGb = clamped;
            if (saveIfChanged)
            {
                RequireSettingsService().Save(settings);
            }
        }

        SetMemoryText(clamped.ToString(CultureInfo.InvariantCulture));
    }

    private static int ClampMemoryGb(int value)
    {
        return MemorySizingService.ClampMemoryGb(value);
    }

    private static int GetAllowedMaxMemoryGb()
    {
        return MemorySizingService.GetAllowedMaxMemoryGb();
    }

    private void SetMemoryText(string text)
    {
        _suppressMemoryTextChanged = true;
        try
        {
            MemoryTextBox.Text = text;
            MemoryTextBox.CaretIndex = MemoryTextBox.Text.Length;
        }
        finally
        {
            _suppressMemoryTextChanged = false;
        }
    }

    private static void CopyTextIfPossible(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        RefreshUi();
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            RefreshUi();
        }
    }

    private async Task RunVoiceActionAsync(Func<Task> action)
    {
        if (_voiceBusy)
        {
            return;
        }

        _voiceBusy = true;
        RefreshUi();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            RequireLogger().Warn(ex.Message);
            MessageBox.Show(ex.Message, "Minecraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _voiceBusy = false;
            RefreshUi();
        }
    }

    private void SetState(string state)
    {
        _state = state;
    }

    private void SetProgressActivity(ProgressBar progressBar, bool active)
    {
        progressBar.Foreground = (Brush)FindResource(active ? "ProgressActiveBrush" : "ProgressIdleBrush");
    }

    private void RefreshUi()
    {
        if (_settings is null) return;

        RefreshPlayerIdentityDisplay();
        var interactiveEnabled = !_busy;
        var configurationEnabled = interactiveEnabled &&
                                   !_transferActive &&
                                   !_minecraftRunning &&
                                   !_minecraftPreparing;
        var voiceEnabled = !_voiceBusy && _voiceChannel is not null;
        var hasBuild = BuildComboBox.SelectedItem is ClientBuildViewModel;
        var selectedRecipient = OnlinePlayerComboBox.SelectedItem as PeerViewModel;
        PlayerNameTextBox.IsEnabled = configurationEnabled;
        PlayerNameTextBox.IsReadOnly = !_isEditingPlayerName || !configurationEnabled;
        ChangePlayerNameButton.IsEnabled = configurationEnabled;
        ChangePlayerNameButton.Content = _isEditingPlayerName ? "Сохранить" : "Изменить";
        BuildComboBox.IsEnabled = configurationEnabled && _builds.Count > 0;
        HostComboBox.IsEnabled = configurationEnabled && _hostPeers.Count > 0;
        PlayButton.Content = "Играть";
        PlayButton.IsEnabled = configurationEnabled && hasBuild && !_isEditingPlayerName;
        SkinButton.IsEnabled = !_minecraftRunning;
        InstallNetworkToolButton.IsEnabled = !_networkToolInstallBusy && !_networkToolInstalled;
        GenerateNetworkCredentialsButton.IsEnabled = interactiveEnabled;
        CopyNetworkNameButton.IsEnabled = interactiveEnabled && !string.IsNullOrWhiteSpace(_settings.NetworkName);
        CopyNetworkPasswordButton.IsEnabled = interactiveEnabled && !string.IsNullOrWhiteSpace(_settings.NetworkPassword);
        NetworkToolAutoLaunchCheckBox.IsEnabled = interactiveEnabled;
        NetworkProviderComboBox.IsEnabled = !_transferActive && _networkProviders.Count > 0;
        WorldComboBox.IsEnabled = interactiveEnabled && !_minecraftPreparing && _worlds.Count > 0;
        OnlinePlayerComboBox.IsEnabled = interactiveEnabled && !_minecraftPreparing && _peers.Count > 0;
        WorldPlaceholderText.Visibility = _worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlinePlayerPlaceholderText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferButton.IsEnabled = interactiveEnabled && !_transferActive && !_minecraftRunning && !_minecraftPreparing &&
                                   WorldComboBox.SelectedItem is WorldViewModel &&
                                   selectedRecipient is not null &&
                                   !selectedRecipient.IsMinecraftRunning &&
                                   !selectedRecipient.IsMinecraftPreparing;
        MemoryTextBox.IsEnabled = configurationEnabled;
        UpdateButton.IsEnabled = interactiveEnabled && !_updateBusy && _preparedUpdate is not null;
        SecretOpenButton.IsEnabled = interactiveEnabled &&
                                     SecretExtendedProgressBar.Visibility != Visibility.Visible &&
                                     SecretProgressBar.Value >= SecretProgressBar.Maximum;

        VoiceJoinButton.IsEnabled = !_voiceBusy && _voiceChannel is not null;
        VoiceSettingsButton.IsEnabled = voiceEnabled;
        VoiceProtectionButton.IsEnabled = _voiceChannel is { IsJoined: true } && !_voiceBusy;
        VoicePttButton.IsEnabled = _voiceChannel is { IsJoined: true };
        VoiceInputComboBox.IsEnabled = voiceEnabled;
        VoiceOutputComboBox.IsEnabled = voiceEnabled;
        VoiceMasterVolumeSlider.IsEnabled = voiceEnabled;
        VoiceMuteButton.IsEnabled = _voiceChannel is { IsJoined: true } && !_voiceBusy;
        VoiceDeafenButton.IsEnabled = _voiceChannel is not null;
        if (_voiceChannel is not null)
        {
            VoiceJoinButton.Content = _voiceChannel.IsJoined ? "\uE778" : "\uE717";
            VoiceJoinButton.ToolTip = _voiceChannel.IsJoined
                ? "Выйти из голосового канала"
                : "Войти в голосовой канал";
            VoiceMuteButton.Content = _voiceChannel.IsMuted ? "\uE198" : "\uE720";
            VoiceMuteButton.ToolTip = _voiceChannel.IsMuted ? "Включить микрофон" : "Выключить микрофон";
            var protectionEnabled = _voiceChannel.IsTrafficProtectionEnabled;
            VoiceProtectionIcon.Text = protectionEnabled ? "\uEA18" : "\uEB59";
            VoiceProtectionIconTransform.Matrix = protectionEnabled
                ? Matrix.Identity
                : DisabledVoiceProtectionIconTransform;
            VoiceProtectionButton.ToolTip = _voiceChannel.IsTrafficProtectionEnabled
                ? "Буфер включён"
                : "Буфер выключен";
            VoiceDeafenButton.Content = "Звук";
        }
        else
        {
            VoiceJoinButton.Content = "\uE717";
            VoiceMuteButton.Content = "\uE720";
            VoiceProtectionIcon.Text = "\uEA18";
            VoiceProtectionIconTransform.Matrix = Matrix.Identity;
            VoiceProtectionButton.ToolTip = "Буфер включён";
            VoiceDeafenButton.Content = "Звук";
        }
        VoiceStatusText.Text = string.Empty;
        RefreshVoiceSettingsWindow();
        RefreshTransferStatus();
    }

    private void RefreshTransferStatus()
    {
        TransferStatusText.Text = string.Empty;
    }

    private void AppendLog(string line)
    {
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private AppPaths RequirePaths() => _paths ?? throw new InvalidOperationException("App paths are not initialized.");
    private AppSettings RequireSettings() => _settings ?? throw new InvalidOperationException("Settings are not initialized.");
    private SettingsService RequireSettingsService() => _settingsService ?? throw new InvalidOperationException("Settings service is not initialized.");
    private Logger RequireLogger() => _logger ?? throw new InvalidOperationException("Logger is not initialized.");
    private VirtualNetworkService RequireNetwork() => _network ?? throw new InvalidOperationException("Network service is not initialized.");
    private NetworkToolSetupService RequireNetworkToolSetup() => _networkToolSetup ?? throw new InvalidOperationException("Network tool setup service is not initialized.");
    private PackHashService RequirePackHash() => _packHash ?? throw new InvalidOperationException("Pack hash service is not initialized.");
    private WorldMetadataService RequireWorldMetadata() => _worldMetadata ?? throw new InvalidOperationException("World metadata service is not initialized.");
    private LocalIdentityService RequireIdentityService() => _identityService ?? throw new InvalidOperationException("Identity service is not initialized.");
    private WorldPlayerProfileService RequireWorldPlayerProfiles() => _worldPlayerProfiles ?? throw new InvalidOperationException("World player profile service is not initialized.");
    private PeerDiscoveryService RequireDiscovery() => _discovery ?? throw new InvalidOperationException("Peer discovery is not initialized.");
    private MinecraftProcessService RequireMinecraft() => _minecraft ?? throw new InvalidOperationException("Minecraft service is not initialized.");
    private WorldTransferService RequireTransfer() => _transfer ?? throw new InvalidOperationException("World transfer service is not initialized.");
    private WaypointSyncService RequireWaypointSync() => _waypointSync ?? throw new InvalidOperationException("Waypoint sync service is not initialized.");
    private SkinService RequireSkinService() => _skinService ?? throw new InvalidOperationException("Skin service is not initialized.");

    private sealed class VoicePresenceEntry
    {
        public VoicePresenceEntry(
            PeerViewModel peer,
            IReadOnlyList<PeerEndpointInfo> endpoints,
            DateTimeOffset lastSeenUtc)
        {
            Peer = peer;
            Endpoints = endpoints;
            LastSeenUtc = lastSeenUtc;
        }

        public PeerViewModel Peer { get; }
        public IReadOnlyList<PeerEndpointInfo> Endpoints { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
    }
    private UpdateService RequireUpdateService() => _updateService ?? throw new InvalidOperationException("Update service is not initialized.");
}
