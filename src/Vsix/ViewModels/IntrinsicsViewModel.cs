using Disasmo.Utils;
using GalaSoft.MvvmLight;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Disasmo.ViewModels;

public class IntrinsicsViewModel : ViewModelBase
{
    private string _input;
    private List<IntrinsicsInfo> _suggestions;
    private List<IntrinsicsInfo> _intrinsics;
    private bool _isBusy;
    private bool _isDownloading;
    private string _loadingStatus;
        
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    private async Task FetchSourcesAsync()
    {
        if (IsInDesignMode || _isDownloading || _intrinsics?.Any() == true)
            return;

        IsBusy = true;
        _isDownloading = true;
        try
        {
            _intrinsics = await IntrinsicsSourcesService.ParseIntrinsics(statusMessage => LoadingStatus = statusMessage);
            UpdateSuggestions();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
            
        IsBusy = false;
        _isDownloading = false;
        return;
    }

    public string Input
    {
        get => _input;
        set
        {
            Set(ref _input, value);
            FetchSourcesAsync();
            UpdateSuggestions();
        }
    }

    private void UpdateSuggestions()
    {
        if (_intrinsics is null || _input.Length < 3)
        {
            Suggestions = null;
            return;
        }

        Suggestions = _intrinsics.Where(i => i.Contains(_input)).Take(15).ToList();
    }

    public string LoadingStatus
    {
        get => _loadingStatus;
        set => Set(ref _loadingStatus, value);
    }

    public List<IntrinsicsInfo> Suggestions
    {
        get => _suggestions;
        set => Set(ref _suggestions, value);
    }
}