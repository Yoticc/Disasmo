using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaSoft.MvvmLight;

namespace Disasmo;

public class FlowgraphItemViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settingsViewModel;
    private string _imageUrl;
    private string _dotFileUrl;
    private string _name;
    private bool _isBusy;
    private Task _currentLoadImageTask;

    public FlowgraphItemViewModel(SettingsViewModel settingsViewModel)
    {
        _settingsViewModel = settingsViewModel;
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

    public async Task LoadImageAsync(CancellationToken cancellationToken)
    {
        var dotImageUrl = DotFileUrl + ".png";
        if (File.Exists(dotImageUrl))
        {
            ImageUrl = dotImageUrl;
        }
        else
        {
            IsBusy = true;
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
            IsBusy = false;
        }
    }
}