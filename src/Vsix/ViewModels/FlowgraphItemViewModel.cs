using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaSoft.MvvmLight;

namespace Disasmo;

public class FlowgraphItemViewModel : ViewModelBase
{
    private readonly MainViewModel _mainViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private string _imageUrl;
    private string _dotFileUrl;
    private string _name;
    private string _ordinal;
    private bool _isBusy;
    private bool _isInitialized;
    private Task _currentLoadImageTask;

    public FlowgraphItemViewModel(
        MainViewModel mainViewModel, 
        SettingsViewModel settingsViewModel, 
        string ordinal, 
        string name, 
        string dotFileUrl, 
        string imageUrl)
    {
        _mainViewModel = mainViewModel;
        _settingsViewModel = settingsViewModel;
        _ordinal = ordinal;
        _name = name;
        _dotFileUrl = dotFileUrl;
        _imageUrl = imageUrl;
    }

    public string Ordinal
    {
        get => _ordinal;
        set => Set(ref _ordinal, value);
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public bool IsInitialPhase => Name?.Contains("Pre-import") == true;

    public string DotFileUrl
    {
        get => _dotFileUrl;
        set => Set(ref _dotFileUrl, value);
    }

    public string ImageUrl
    {
        get => _imageUrl;
        set => Set(ref _imageUrl, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public bool IsInitialized
    {
        get => _isInitialized && File.Exists(DotFileUrl + ".png");
        set => Set(ref _isInitialized, value);
    }

    public async Task LoadImageAsync(CancellationToken cancellationToken)
    {
        if (!IsBusy)
            _currentLoadImageTask = InternalLoadImageAsync(cancellationToken);

        await _currentLoadImageTask;
    }

    async Task InternalLoadImageAsync(CancellationToken cancellationToken)
    {
        var dotImageUrl = DotFileUrl + ".png";
        if (IsInitialized)
        {
            ImageUrl = dotImageUrl;
        }
        else
        {
            IsBusy = true;
            _mainViewModel.NotifyFlowgraphPhaseLoadingStarted();
            try
            {
                var dotExeArgs = $"-Tpng -o\"{dotImageUrl}\" -Kdot \"{DotFileUrl}\"";
                await ProcessUtils.RunProcess(_settingsViewModel.GraphvisDotPath, dotExeArgs, cancellationToken: cancellationToken);
                ImageUrl = dotImageUrl;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            IsInitialized = true;
            _mainViewModel.NotifyFlowgraphPhaseLoadingFinished();
            IsBusy = false;
        }
    }
}