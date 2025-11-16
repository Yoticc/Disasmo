using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using System.ComponentModel.Composition;

namespace Disasmo.Utils;

// Taken from https://gist.github.com/madskristensen/4d205244dd92c37c82e7
// It is necessary because usual component system... does not work for some reason.
// If you have enough experience, you can try to find out why this happens and then remove this crutch.
public static class MefExtensions
{
    private static IComponentModel _compositionService;

    private static IComponentModel compositionService
    {
        get
        {
            if (_compositionService is null)
            {
                _compositionService = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            }

            return _compositionService;
        }
    }

    public static void SatisfyImportsOnce(object o)
    {
        compositionService.DefaultCompositionService.SatisfyImportsOnce(o);
    }
}