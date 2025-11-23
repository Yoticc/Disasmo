using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Process = System.Diagnostics.Process;

namespace Disasmo;

public static class IdeUtils
{
    public static DTE DTE() => Package.GetGlobalService(typeof(SDTE)) as DTE;

    public static Project GetActiveProject(this DTE dte, string filePath)
    {
        // Find project by full name
        if (dte.Solution is not null)
        {
            foreach (var projectObject in dte.Solution.Projects)
            {
                if (projectObject is Project project && project.FullName == filePath)
                {
                    return project;
                }
            }
        }

        var activeSolutionProjects = dte.ActiveSolutionProjects as Array;
        if (activeSolutionProjects is not null && activeSolutionProjects.Length > 0)
            return activeSolutionProjects.GetValue(0) as Project;

        return null;
    }

    public static void SaveActiveDocument(this DTE dte)
    {
        try
        {
            dte.ActiveDocument?.Save();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static void SaveAllDocuments(this DTE dte)
    {
        try
        {
            foreach (Document document in dte.Documents)
                document?.Save();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static async Task<T> ShowWindowAsync<T>(bool tryTwice, CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (DisasmoPackage.Current is null)
            {
                MessageBox.Show("DisasmoPackage is still loading... (sometimes it takes a while for add-ins to fully load - it makes VS faster to start).");
                return null;
            }

            await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var window = await DisasmoPackage.Current.ShowToolWindowAsync(typeof(DisasmWindow), 0, create: true, cancellationToken: cancellationToken);

            if (tryTwice)
            {
                await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                // No idea why I have to call it twice, it doesn't work if I do it only once on the first usage
                window = await DisasmoPackage.Current.ShowToolWindowAsync(typeof(T), 0, create: true, cancellationToken: cancellationToken);
                await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            }

            return window as T;
        }
        catch
        {
            return null;
        }
    }

    public static void RunDiffTools(string contentLeft, string contentRight)
    {
        contentLeft = string.IsNullOrEmpty(contentLeft) ? " " : contentLeft;
        contentRight = string.IsNullOrEmpty(contentRight) ? " " : contentRight;

        var tempPath = Path.GetTempPath();
        var diffDirectory = Path.Combine(tempPath, "Disasmo_diffs_" + Guid.NewGuid().ToString("N").Substring(0, 10));
        Directory.CreateDirectory(diffDirectory);

        var tempFileLeft = Path.Combine(diffDirectory, "previous.asm");
        var tempFileRight = Path.Combine(diffDirectory, "current.asm");

        File.WriteAllText(tempFileLeft, contentLeft.NormalizeLineEndings());
        File.WriteAllText(tempFileRight, contentRight.NormalizeLineEndings());

        try
        {
            // Copied from https://github.com/madskristensen/FileDiffer/blob/master/src/Commands/SelectedFilesCommand.cs#L62-L71 (c) madskristensen
            object args = $"\"{tempFileLeft}\" \"{tempFileRight}\"";
            ((DTE)Package.GetGlobalService(typeof(SDTE))).Commands.Raise("5D4C0442-C0A2-4BE8-9B4D-AB1C28450942", ID: 256, ref args, ref args);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            File.Delete(tempFileLeft);
            File.Delete(tempFileRight);
        }
    }

    public static string GetTargetFrameworkDimension(ProjectConfiguration projectConfiguration)
    {
        return projectConfiguration.Dimensions.TryGetValue("TargetFramework", out var targetFramework)
            ? targetFramework
            : null;
    }

    public static string GetConfigurationDimension(ProjectConfiguration projectConfiguration)
    {
        return projectConfiguration.Dimensions.TryGetValue("Configuration", out var configuration)
            ? configuration
            : null;
    }

    public static TfmVersion GetTargetFrameworkVersionDimension(ProjectConfiguration projectConfiguration)
    {
        var targetFramework = GetTargetFrameworkDimension(projectConfiguration);
        return targetFramework is not null ? TfmVersion.Parse(targetFramework) : null;
    }

    public static async Task<IProjectProperties> GetProjectPropertiesAsync(UnconfiguredProject unconfiguredProject, ProjectConfiguration projectConfiguration)
    {
        try
        {
            // It will throw "Release config was not found" to the Output if there is no such config in the project
            projectConfiguration ??= await unconfiguredProject.Services.ProjectConfigurationsService.GetProjectConfigurationAsync("Release");
            var configuredProject = await unconfiguredProject.LoadConfiguredProjectAsync(projectConfiguration);
            return configuredProject.Services.ProjectPropertiesProvider.GetCommonProperties();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            // VS was not able to find the given config (but it still might exist)
            return null;
        }
    }

    public static async Task<IEnumerable<ProjectConfiguration>> GetProjectConfigurationsAsync(UnconfiguredProject unconfiguredProject)
    {
        try
        {
            return await unconfiguredProject.Services
                .ProjectConfigurationsService
                .GetKnownProjectConfigurationsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            // VS was not able to find the given config (but it still might exist)
            return [];
        }
    }

    public static void OpenInVSCode(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        try
        {
            var file = Path.GetTempFileName() + ".txt";
            File.WriteAllText(file, output.NormalizeLineEndings());

            var processStartInfo = new ProcessStartInfo(file);
            processStartInfo.Verb = "open";
            processStartInfo.UseShellExecute = true;
            Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static void OpenInVS(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        try
        {
            // Let's try .asm file and hope VS will be able to apply some highlighting
            // even for JitDump...
            var file = Path.GetTempFileName() + ".asm";
            File.WriteAllText(file, output.NormalizeLineEndings());

            DTE().ItemOperations.OpenFile(file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}