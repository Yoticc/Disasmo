using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Disasmo;

// Builds a module loader app using system dotnet
// Rebuilds it when the Add-in updates or SDK's version changes.
public static class LoaderAppManager
{
    public static readonly string DisasmoLoaderName = "DisasmoLoader4";

    private static async Task<string> GetPathToLoader(
        string targetFramework, 
        Version disasmoVersion, 
        CancellationToken cancellationToken)
    {
        var dotnetVersion = await ProcessUtils.RunProcess("dotnet", "--version", cancellationToken: cancellationToken);
        UserLogger.Log($"dotnet --version: {dotnetVersion.Output} ({dotnetVersion.Error})");
        var version = dotnetVersion.Output.Trim();
        if (!char.IsDigit(version[0]))
        {
            // Something went wrong, use a random to proceed
            version = Guid.NewGuid().ToString("N");
        }
        var folderName = $"{disasmoVersion}_{targetFramework}_{version}";
        UserLogger.Log($"LoaderAppManager.GetPathToLoader: {folderName}");
        return Path.Combine(Path.GetTempPath(), DisasmoLoaderName, folderName);
    }

    public static async Task InitLoaderAndCopyTo(
        string targetFramework, 
        string destination, 
        Action<string> logger, 
        Version disasmoVersion, 
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(destination))
            throw new InvalidOperationException($"ERROR: destination directory was not found: {destination}");

        string directory;
        try
        {
            logger("Getting SDK version...");
            directory = await GetPathToLoader(targetFramework, disasmoVersion, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("ERROR in LoaderAppManager.GetPathToLoader: " + ex);
        }

        var csproj = Path.Combine(directory, $"{DisasmoLoaderName}.csproj");
        var csfile = Path.Combine(directory, $"{DisasmoLoaderName}.cs");
        var outDll = Path.Combine(directory, "out", $"{DisasmoLoaderName}.dll");
        var outJson = Path.Combine(directory, "out", $"{DisasmoLoaderName}.runtimeconfig.json");
        var outDllDestination = Path.Combine(destination, DisasmoLoaderName + ".dll");
        var outJsonDestination = Path.Combine(destination, DisasmoLoaderName + ".runtimeconfig.json");

        if (File.Exists(outDllDestination) && File.Exists(outJsonDestination))
            return;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        else if (File.Exists(outDll) && File.Exists(outJson))
        {
            File.Copy(outDll, outDllDestination, overwrite: true);
            File.Copy(outJson, outJsonDestination, overwrite: true);
            return;
        }

        logger($"Building '{DisasmoLoaderName}' project...");
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(csfile))
            TextUtils.SaveEmbeddedResourceTo($"{DisasmoLoaderName}.cs_template", directory);

        if (!File.Exists(csproj))
            TextUtils.SaveEmbeddedResourceTo($"{DisasmoLoaderName}.csproj_template", directory, content => content.Replace("%tfm%", targetFramework));

        Debug.Assert(File.Exists(csfile));
        Debug.Assert(File.Exists(csproj));

        cancellationToken.ThrowIfCancellationRequested();

        var message = await ProcessUtils.RunProcess("dotnet", "build -c Release", workingDirectory: directory, cancellationToken: cancellationToken);

        if (!File.Exists(outDll) || !File.Exists(outJson))
        {
            var errorMessage = $"ERROR: 'dotnet build' did not produce expected binaries ('{outDll}' and '{outJson}'):\n{message.Output}\n\n{message.Error}";
            throw new InvalidOperationException(errorMessage);
        }            

        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(outDll, outDllDestination, overwrite: true);
        File.Copy(outJson, outJsonDestination, overwrite: true);
    }
}