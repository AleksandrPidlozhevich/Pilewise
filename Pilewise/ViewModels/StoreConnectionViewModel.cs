using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pilewise.Models;
using Pilewise.Services;
using System;
using System.Threading.Tasks;

namespace Pilewise.ViewModels;

public sealed partial class StoreConnectionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowCodeInput))]
    private bool isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowCodeInput))]
    private bool isCodeInputVisible;

    [ObservableProperty] private string? authorizationCode;

    private readonly StoreConnector _connector;
    private readonly bool _supportsCodeInput;
    private readonly string _connectingStatus;
    private readonly Action<string>? _beginBusy;
    private readonly Action? _endBusy;
    private readonly Func<StoreConnectionViewModel, Task>? _onConnected;
    private readonly Action? _openInfo;

    public string Name { get; }

    public string ConnectLabel => $"Connect {Name}";

    public string InfoTip => $"How to connect {Name}";

    public string? CodePlaceholder { get; }

    public bool ShowConnectButton => !IsConnected && !IsCodeInputVisible;

    public bool ShowCodeInput => _supportsCodeInput && !IsConnected && IsCodeInputVisible;

    public bool HasInfo => _openInfo != null;

    public StoreConnectionViewModel(
        string name,
        Func<IStoreClient> clientFactory,
        Func<string?, string?> extractCode,
        Func<IClipboard?> getClipboard,
        Func<string, Exception, Task> showError,
        ILogService log,
        IWebAuthenticator? webAuth = null,
        bool supportsCodeInput = false,
        string? codePlaceholder = null,
        string? connectingStatus = null,
        Action<string>? beginBusy = null,
        Action? endBusy = null,
        Func<StoreConnectionViewModel, Task>? onConnected = null,
        Action? openInfo = null)
    {
        Name = name;
        CodePlaceholder = codePlaceholder;
        _supportsCodeInput = supportsCodeInput;
        _connectingStatus = connectingStatus ?? $"Opening {name} login…";
        _beginBusy = beginBusy;
        _endBusy = endBusy;
        _onConnected = onConnected;
        _openInfo = openInfo;

        _connector = new StoreConnector(
            name,
            clientFactory,
            extractCode,
            v => IsConnected = v,
            v => IsCodeInputVisible = v,
            c => AuthorizationCode = c,
            () => IsConnected,
            getClipboard,
            showError,
            log,
            webAuth);
    }

    public Task InitializeFromCacheAsync() => _connector.InitializeFromCacheAsync();

    [RelayCommand]
    private async Task StartConnect()
    {
        _beginBusy?.Invoke(_connectingStatus);
        try
        {
            await _connector.StartConnectAsync();

            if (IsConnected && _onConnected != null)
                await _onConnected(this);
        }
        finally
        {
            _endBusy?.Invoke();
        }
    }

    [RelayCommand]
    private void Disconnect() => _connector.SignOut();

    [RelayCommand]
    private void OpenInfo() => _openInfo?.Invoke();

    partial void OnAuthorizationCodeChanged(string? value)
    {
        _connector.OnCodeChanged(value);
    }
}
