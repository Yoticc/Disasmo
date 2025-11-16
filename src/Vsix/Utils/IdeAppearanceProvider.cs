using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Windows.Media;

namespace Disasmo.Utils;

public static class IdeAppearanceProvider
{
    private static bool GetStorageService(out IVsFontAndColorStorage service)
    {
        service = Package.GetGlobalService(typeof(SVsFontAndColorStorage)) as IVsFontAndColorStorage;
        if (service is null)
        {
            Log("Failed to get global service SVsFontAndColorStorage");
            return false;
        }

        return true;
    }

    private static bool OpenStorageCategory(IVsFontAndColorStorage storageService, Guid categoryGuid)
    {
        var hresult = storageService.OpenCategory(ref categoryGuid, (uint)(__FCSTORAGEFLAGS.FCSF_READONLY | __FCSTORAGEFLAGS.FCSF_LOADDEFAULTS));
        if (hresult < 0)
        {
            Log($"Failed to open category with guid {categoryGuid}");
            return false;
        }

        return true;
    }

    private static bool GetStorageColorableItemInfo(IVsFontAndColorStorage storageService, string itemName, ref ColorableItemInfo info)
    {
        var infoArray = new ColorableItemInfo[1];
        var hresult = storageService.GetItem(itemName, infoArray);
        if (hresult < 0)
        {
            Log($"Failed to get FontAndColorStorageService item with name \"{itemName}\"");

            info = default;
            return false;
        }

        info = infoArray[0];

        var a = System.Drawing.ColorTranslator.FromOle((int)info.crForeground);

        return true;
    }

    // I hope opening a category on every call wont cause performance issues.
    // If it causes, implement a method to get multiple elements from one category at the same time.
    public static bool GetColorableItemInfo(Guid categoryGuid, string itemName, ref ColorableItemInfo info)
    {
        if (GetStorageService(out var storageService))
        {
            if (OpenStorageCategory(storageService, categoryGuid))
            {
                var result = GetStorageColorableItemInfo(storageService, itemName, ref info);
                storageService.CloseCategory();

                return result;
            }
        }

        return false;
    }

    public static bool GetColorItem(Guid categoryGuid, string itemName, bool foreground, ref Color color)
    {
        ColorableItemInfo info = default;
        if (!GetColorableItemInfo(categoryGuid, itemName, ref info))
        {
            Log($"Failed to obtain ide editor color with name \"{itemName}\"");
            return false;
        }

        var colorInteger = foreground ? info.crForeground : info.crBackground;
        color = ConvertIntegerToColor(colorInteger);

        return true;
    }

    private static Color ConvertIntegerToColor(uint color)
    {
        return Color.FromArgb(a: 255, r: (byte)color, g: (byte)(color >> 8), b: (byte)(color >> 16));
    }

    private static void Log(string message)
    {
        UserLogger.Log($"{nameof(IdeAppearanceProvider)}: {message}");
    }
}