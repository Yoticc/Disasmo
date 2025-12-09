using Disasmo.ViewModels;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using CAProject = Microsoft.CodeAnalysis.Project;
using Project = EnvDTE.Project;
using Task = System.Threading.Tasks.Task;

namespace Disasmo;

public class MainViewModel : ViewModelBase
{
    private static readonly int MaxCountOfLoadingPhases = Math.Max(2, Environment.ProcessorCount / 2);
    // dot.exe eats exactly one virtual core. Assuming that the number of
    // virtual cores (Environment.ProcessorCount) equals to the number of 
    // physical cores multiplied to 2. 
    // By the expression above, we allow dot.exe to use only 50% of the cpu resources.
    // If don't take the limitation into account, dot.exe will eat up all the cpu resources,
    // leading to at least system interruptions, and at worst to a system crash.

    private string _output;
    private string _previousOutput;
    private string _loadingStatus;
    private string _stopwatchStatus;
    private string[] _jitDumpPhases;
    private bool _isLoading;
    private bool _isPhasesLoading;
    private bool _lastJitDumpStatus;
    private ISymbol _currentSymbol;
    private CAProject _currentProject;
    private bool _success;
    private string _currentProjectPath;
    private string _currentTargetFramework;
    private string _flowgraphPngPath;
    private bool _updateIsAvailable;
    private string DisasmoOutputDirectory = "";
    private ObservableCollection<FlowgraphItemViewModel> _flowgraphPhases = new();
    private FlowgraphItemViewModel _selectedPhase;
    private int _countOfLoadingPhases;
    private string _phaseLoadingPhrase;
    private Version _currentVersion;
    private Version _availableVersion;

    // Let's use new name for the temp folder each version to avoid possible issues (e.g. changes in the Disasmo.Loader)
    private string DisasmoFolder => "Disasmo-v" + DisasmoPackage.Current?.GetCurrentVersion();

    public SettingsViewModel SettingsViewModel { get; } = new();
    public IntrinsicsViewModel IntrinsicsViewModel { get; } = new();

    public event Action MainPageRequested;

    public string[] JitDumpPhases
    {
        get => _jitDumpPhases;
        set => Set(ref _jitDumpPhases, value);
    }

    public string Output
    {
        get => _output;
        set
        {
            if (!string.IsNullOrWhiteSpace(_output))
                PreviousOutput = _output;
            Set(ref _output, value);

            const string phasePrefix = "*************** Starting PHASE ";

            if (_output is not null)
            {
                JitDumpPhases = Output
                    .Split('\n')
                    .Where(l => l.StartsWith(phasePrefix))
                    .Select(i => i.Replace(phasePrefix, ""))
                    .ToArray();
            }
            else
            {
                JitDumpPhases = [];
            }
        }
    }

    public string PreviousOutput
    {
        get => _previousOutput;
        set => Set(ref _previousOutput, value);
    }

    public string LoadingStatus
    {
        get => _loadingStatus;
        set => Set(ref _loadingStatus, value);
    }

    public CancellationTokenSource UserCancellationTokens { get; set; }

    public CancellationToken UserCancellationToken => UserCancellationTokens?.Token ?? default;

    public void ThrowIfCanceled()
    {
        if (UserCancellationTokens?.IsCancellationRequested == true)
            throw new OperationCanceledException();
    }

    public ICommand CancelCommand => new RelayCommand(() =>
    {
        try { UserCancellationTokens?.Cancel(); } catch { }
    });

    public string DefaultHotKey => DisasmoPackage.HotKey;

    public bool UpdateIsAvailable
    {
        get => _updateIsAvailable;
        set { Set(ref _updateIsAvailable, value); }
    }

    public bool Success
    {
        get => _success;
        set => Set(ref _success, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (!_isLoading && value)
            {
                UserCancellationTokens = new CancellationTokenSource();
            }
            Set(ref _isLoading, value);
        }
    }

    public bool IsPhasesLoading
    {
        get => _isPhasesLoading;
        set
        {
            Set(ref _isPhasesLoading, value);
        }
    }

    public string StopwatchStatus
    {
        get => _stopwatchStatus;
        set => Set(ref _stopwatchStatus, value);
    }

    public string FlowgraphPngPath
    {
        get => _flowgraphPngPath;
        set => Set(ref _flowgraphPngPath, value);
    }

    public ICommand RefreshCommand => new RelayCommand(() => RunOperationAsync(_currentSymbol, _currentProject));

    public ICommand RunDiffWithPrevious => new RelayCommand(() => IdeUtils.RunDiffTools(PreviousOutput, Output));

    public ICommand OpenInVSCode => new RelayCommand(() => IdeUtils.OpenInVSCode(Output));

    public ICommand OpenInVS => new RelayCommand(() => IdeUtils.OpenInVS(Output));

    public ObservableCollection<FlowgraphItemViewModel> FlowgraphPhases
    {
        get => _flowgraphPhases;
        set => Set(ref _flowgraphPhases, value);
    }

    public FlowgraphItemViewModel SelectedPhase
    {
        get => _selectedPhase;
        set
        {
            Set(ref _selectedPhase, value);

            TryLoadSelectedPhaseImage();
        }
    }

    public bool LastContextIsAsm => Success && !_lastJitDumpStatus;

    public string PhaseLoadingPhrase
    {
        get => _phaseLoadingPhrase;
        set => Set(ref _phaseLoadingPhrase, value);
    }

    public Version CurrentVersion
    {
        get => _currentVersion;
        set => Set(ref _currentVersion, value);
    }

    public Version AvailableVersion
    {
        get => _availableVersion;
        set => Set(ref _availableVersion, value);
    }

    public async Task CheckUpdatesAsync()
    {
        CurrentVersion = DisasmoPackage.Current?.GetCurrentVersion();
        AvailableVersion = await DisasmoPackage.Current?.GetLatestVersionOnlineAsync();
        if (CurrentVersion != null && AvailableVersion != null && AvailableVersion > CurrentVersion)
            UpdateIsAvailable = true;
    }

    public async Task RunFinalExeAsync(DisasmoSymbolInfo symbolInfo, IProjectProperties projectProperties)
    {
        try
        {
            if (_currentSymbol is null || string.IsNullOrWhiteSpace(_currentProjectPath))
                return;

            await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync();

            Success = false;
            IsLoading = true;
            FlowgraphPngPath = null;
            LoadingStatus = "Loading...";
            _lastJitDumpStatus = SettingsViewModel.JitDumpInsteadOfDisasm;

            var destinationFolder = DisasmoOutputDirectory;
            if (!Path.IsPathRooted(destinationFolder))
                destinationFolder = Path.Combine(Path.GetDirectoryName(_currentProjectPath), destinationFolder);

            // TODO: Respect AssemblyName property (if it doesn't match csproj name)
            var fileName = Path.GetFileNameWithoutExtension(_currentProjectPath);

            try
            {
                if (projectProperties is not null)
                {
                    var customAsmName = await projectProperties.GetEvaluatedPropertyValueAsync("AssemblyName");
                    if (!string.IsNullOrWhiteSpace(customAsmName))
                    {
                        fileName = customAsmName;
                    }
                }
            }
            catch { }

            var envVars = new Dictionary<string, string>();

            if (!SettingsViewModel.RunAppMode && !SettingsViewModel.CrossgenIsSelected && !SettingsViewModel.NativeAotIsSelected)
            {
                var disasmoVersion = DisasmoPackage.Current.GetCurrentVersion();
                await LoaderAppManager.InitLoaderAndCopyTo(_currentTargetFramework, destinationFolder, log => { /*TODO: Update UI*/ }, disasmoVersion, UserCancellationToken);
            }

            if (SettingsViewModel.JitDumpInsteadOfDisasm)
            {
                envVars["DOTNET_JitDump"] = symbolInfo.Target;
            }
            else if (SettingsViewModel.PrintInlinees)
            {
                envVars["DOTNET_JitPrintInlinedMethods"] = symbolInfo.Target;
            }
            else
            {
                envVars["DOTNET_JitDisasm"] = symbolInfo.Target;
            }

            if (!string.IsNullOrWhiteSpace(SettingsViewModel.SelectedCustomJit) &&
                !SettingsViewModel.CrossgenIsSelected && 
                !SettingsViewModel.NativeAotIsSelected &&
                !SettingsViewModel.SelectedCustomJit.Equals(Constants.DefaultJit, StringComparison.InvariantCultureIgnoreCase) &&
                SettingsViewModel.UseCustomRuntime)
            {
                envVars["DOTNET_AltJitName"] = SettingsViewModel.SelectedCustomJit;
                envVars["DOTNET_AltJit"] = symbolInfo.Target;
            }

            envVars["DOTNET_TieredPGO"] = SettingsViewModel.UsePGO ? "1" : "0";
            envVars["DOTNET_JitDisasmDiffable"] = SettingsViewModel.Diffable ? "1" : "0";

            if (!SettingsViewModel.UseDotnetPublishForReload && SettingsViewModel.UseCustomRuntime)
            {
                var (runtimePackPath, success) = GetPathToRuntimePack();
                if (!success)
                    return;

                // Tell jit to look for BCL libs in the locally built runtime pack
                envVars["CORE_LIBRARIES"] = runtimePackPath;
            }

            envVars["DOTNET_TieredCompilation"] = SettingsViewModel.UseTieredJit ? "1" : "0";

            // User is free to override any of those ^
            SettingsViewModel.FillWithUserVars(envVars);

            string currentFlowgraphFile = null;
            if (SettingsViewModel.FlowgraphEnable)
            {
                if (symbolInfo.MethodName == "*")
                {
                    Output = "Flowgraph for classes (all methods) is not supported yet.";
                    return;
                }

                currentFlowgraphFile = Path.GetTempFileName();
                envVars["DOTNET_JitDumpFg"] = symbolInfo.Target;
                envVars["DOTNET_JitDumpFgDot"] = "1";
                envVars["DOTNET_JitDumpFgPhase"] = "*";
                envVars["DOTNET_JitDumpFgFile"] = currentFlowgraphFile;
            }

            var command = $"\"{LoaderAppManager.DisasmoLoaderName}.dll\" \"{fileName}.dll\" \"{symbolInfo.ClassName}\" \"{symbolInfo.MethodName}\" {SettingsViewModel.UseUnloadableContext}";
            if (SettingsViewModel.RunAppMode)
            {
                command = $"\"{fileName}.dll\"";
            }

            var executable = "dotnet";
            if (SettingsViewModel.CrossgenIsSelected && SettingsViewModel.UseCustomRuntime)
            {
                var (clrCheckedFilesDir, checkedFound) = GetPathToCoreClrChecked();
                if (!checkedFound)
                    return;

                var (runtimePackPath, runtimePackFound) = GetPathToRuntimePack();
                if (!runtimePackFound)
                    return;

                executable = Path.Combine(SettingsViewModel.PathToLocalCoreClr, "dotnet.cmd");
                command = $"{Path.Combine(clrCheckedFilesDir, "crossgen2", "crossgen2.dll")} --out aot ";

                foreach (var envVar in envVars)
                {
                    var keyLower = envVar.Key.ToLowerInvariant();
                    if (keyLower?.StartsWith("dotnet_") == false &&
                        keyLower?.StartsWith("complus_") == false)
                    {
                        continue;
                    }

                    keyLower = keyLower
                        .Replace("dotnet_jitdump", "--codegenopt:jitdump")
                        .Replace("dotnet_jitdisasm", "--codegenopt:jitdisasm")
                        .Replace("dotnet_", "--codegenopt:")
                        .Replace("complus_", "--codegenopt:");
                    command += keyLower + "=\"" + envVar.Value + "\" ";
                }
                envVars.Clear();

                // These are needed for faster crossgen itself - they're not changing output codegen
                envVars["DOTNET_TieredPGO"] = "0";
                envVars["DOTNET_ReadyToRun"] = "1";
                envVars["DOTNET_TC_QuickJitForLoops"] = "1";
                envVars["DOTNET_TC_CallCountingDelayMs"] = "0";
                envVars["DOTNET_TieredCompilation"] = "1";
                command += SettingsViewModel.Crossgen2Args.Replace("\r\n", " ").Replace("\n", " ") + $" \"{fileName}.dll\" ";

                if (SettingsViewModel.UseDotnetPublishForReload)
                {
                    // Reference everything in the publish dir
                    command += $" -r: \"{destinationFolder}\\*.dll\" ";
                }
                else
                {
                    // The runtime pack we use doesn't contain corelib so let's use "checked" corelib
                    // TODO: Build proper core_root with release version of corelib
                    var corelib = Path.Combine(clrCheckedFilesDir, "System.Private.CoreLib.dll");
                    command += $" -r: \"{runtimePackPath}\\*.dll\" -r: \"{corelib}\" ";
                }

                LoadingStatus = $"Executing crossgen2...";
            }
            else if (SettingsViewModel.NativeAotIsSelected && SettingsViewModel.UseCustomRuntime)
            {
                var (clrReleaseFolder, clrFound) = GetPathToCoreClrCheckedForNativeAot();
                if (!clrFound)
                    return;

                command = "";
                executable = Path.Combine(clrReleaseFolder, "ilc", "ilc.exe");

                command += $" \"{fileName}.dll\" ";

                foreach (var envVar in envVars)
                {
                    var keyLower = envVar.Key.ToLowerInvariant();
                    if (keyLower?.StartsWith("dotnet_") == false &&
                        keyLower?.StartsWith("complus_") == false)
                    {
                        continue;
                    }

                    keyLower = keyLower
                        .Replace("dotnet_jitdump", "--codegenopt:jitdump")
                        .Replace("dotnet_jitdisasm", "--codegenopt:jitdisasm")
                        .Replace("dotnet_", "--codegenopt:")
                        .Replace("complus_", "--codegenopt:");
                    command += keyLower + "=\"" + envVar.Value + "\" ";
                }
                envVars.Clear();
                command += SettingsViewModel.IlcArgs.Replace("%DOTNET_REPO%", SettingsViewModel.PathToLocalCoreClr.TrimEnd('\\', '/')).Replace("\r\n", " ").Replace("\n", " ");

                if (SettingsViewModel.UseDotnetPublishForReload)
                {
                    // Reference everything in the publish dir
                    command += $" -r: \"{destinationFolder}\\*.dll\" ";
                }
                else
                {
                    // The runtime pack we use doesn't contain corelib so let's use "checked" corelib.
                    // TODO: Build proper core_root with release version of corelib
                    //var corelib = Path.Combine(clrCheckedFilesDir, "System.Private.CoreLib.dll");
                    //command += $" -r: \"{runtimePackPath}\\*.dll\" -r: \"{corelib}\" ";
                }

                LoadingStatus = "Executing ILC... Make sure your method is not inlined and is reachable as NativeAOT runs IL Link. It might take some time...";
            }
            else if (SettingsViewModel.IsNonCustomNativeAOTMode())
            {
                LoadingStatus = "Compiling for NativeAOT (.NET 8.0+ is required) ...";

                // For non-custom NativeAOT we need to use dotnet publish + with custom IlcArgs.
                // Namely, we need to re-direct jit's output to a file (JitStdOutFile)

                var tmpJitStdout = Path.GetTempFileName() + ".asm";

                envVars["DOTNET_JitStdOutFile"] = tmpJitStdout;

                var customIlcArgs = "";
                foreach (var envVar in envVars)
                {
                    var keyLower = envVar.Key.ToLowerInvariant();
                    if (keyLower?.StartsWith("dotnet_") == false &&
                        keyLower?.StartsWith("complus_") == false)
                    {
                        continue;
                    }

                    keyLower = keyLower
                        .Replace("dotnet_", "--codegenopt:")
                        .Replace("complus_", "--codegenopt:");
                    customIlcArgs += $"\t\t<IlcArg Include=\"{keyLower}=&quot;{envVar.Value}&quot;\" />\n";
                }
                envVars.Clear();

                var tmpProps = Path.GetTempFileName() + ".props";
                File.WriteAllText(tmpProps, $"""
                                            <?xml version="1.0" encoding="utf-8"?>
                                            <Project>
                                                <PropertyGroup>
                                                    <DefineConstants>$(DefineConstants);DISASMO</DefineConstants>
                                                </PropertyGroup>
                                            	<ItemGroup>
                                            {customIlcArgs}
                                            	</ItemGroup>
                                            </Project>
                                            """);

                var targetFrameworkPart = SettingsViewModel.DontGuessTargetFramework && string.IsNullOrWhiteSpace(SettingsViewModel.OverridenTargetFramework) ? "" : $"-f {_currentTargetFramework}";

                // NOTE: CustomBeforeDirectoryBuildProps is probably not a good idea to overwrite, but we need to pass IlcArgs somehow
                var dotnetPublishArgs =
                    $"publish {targetFrameworkPart} -r win-{SettingsViewModel.Arch} -c Release" +
                    $" /p:PublishAot=true /p:CustomBeforeDirectoryBuildProps=\"{tmpProps}\"" +
                    $" /p:WarningLevel=0 /p:TreatWarningsAsErrors=false -v:q";

                var publishResult = await ProcessUtils.RunProcess("dotnet", dotnetPublishArgs, null, Path.GetDirectoryName(_currentProjectPath), cancellationToken: UserCancellationToken);

                ThrowIfCanceled();

                if (string.IsNullOrEmpty(publishResult.Error))
                {
                    if (!File.Exists(tmpJitStdout))
                    {
                        Output = $"""
                                  JitDisasm didn't produce any output :(. Make sure your method is not inlined by the code generator
                                  (it's a good idea to mark it as [MethodImpl(MethodImplOptions.NoInlining)]) and is reachable from Main() as
                                  NativeAOT may delete unused methods. Also, JitDisasm doesn't work well for Main() in NativeAOT mode."


                                  {publishResult.Output}
                                  """;
                    }
                    else
                    {
                        Success = true;
                        Output = File.ReadAllText(tmpJitStdout);

                        // Keep the temp files around for debugging if it failed.
                        // And delete them if it succeeded.
                        File.Delete(tmpProps);
                        File.Delete(tmpJitStdout);
                    }
                }
                else
                {
                    Output = publishResult.Error;
                }

                return;
            }
            else
            {
                LoadingStatus = $"Executing DisasmoLoader...";
            }

            if (!SettingsViewModel.UseDotnetPublishForReload &&
                !SettingsViewModel.CrossgenIsSelected &&
                !SettingsViewModel.NativeAotIsSelected &&
                SettingsViewModel.UseCustomRuntime)
            {
                var (clrCheckedFilesDir, success) = GetPathToCoreClrChecked();
                if (!success)
                    return;

                executable = Path.Combine(clrCheckedFilesDir, "CoreRun.exe");
            }

            if (SettingsViewModel.RunAppMode && 
                !string.IsNullOrWhiteSpace(SettingsViewModel.OverridenJitDisasm))
            {
                envVars["DOTNET_JitDisasm"] = SettingsViewModel.OverridenJitDisasm;
            }

            var result = await ProcessUtils.RunProcess(executable, command, envVars, destinationFolder, cancellationToken: UserCancellationToken);
            ThrowIfCanceled();

            if (string.IsNullOrEmpty(result.Error))
            {
                Success = true;
                Output = PreprocessOutput(result.Output);
            }
            else
            {
                Output = result.Output + "\nERROR:\n" + result.Error;
            }

            if (SettingsViewModel.FlowgraphEnable && SettingsViewModel.JitDumpInsteadOfDisasm)
            {
                currentFlowgraphFile += ".dot";
                if (!File.Exists(currentFlowgraphFile))
                {
                    Output = $"Oops, JitDumpFgFile ('{currentFlowgraphFile}') doesn't exist :(\nInvalid Phase name?";
                    return;
                }

                if (new FileInfo(currentFlowgraphFile).Length == 0)
                {
                    Output = $"Oops, JitDumpFgFile ('{currentFlowgraphFile}') file is empty :(\nInvalid Phase name?";
                    return;
                }

                var flowGraphLines = File.ReadAllText(currentFlowgraphFile);

                FlowgraphPhases.Clear();
                var graphs = flowGraphLines.Split(["digraph FlowGraph {"], StringSplitOptions.RemoveEmptyEntries);
                var graphIndex = 0;
                var absoluteGraphIndex = 0;

                var flowgraphBaseDirectory = Path.Combine(Path.GetTempPath(), "Disasmo", "Flowgraphs", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(flowgraphBaseDirectory);
                foreach (var graph in graphs)
                {
                    try
                    {
                        var name = graph.Substring(graph.IndexOf("graph [label = ") + "graph [label = ".Length);
                        name = name.Substring(0, name.IndexOf("\"];"));
                        name = name.Replace("\\n", " ");
                        name = name.Substring(name.IndexOf(" after ") + " after ".Length).Trim();

                        // Reset counter if tier0 and tier1 are merged together
                        if (name == "Pre-import")
                        {
                            graphIndex = 0;
                        }

                        graphIndex++;
                        absoluteGraphIndex++;

                        // Ignore invalid path chars
                        name = Path.GetInvalidFileNameChars().Aggregate(name, (current, ic) => current.Replace(ic, '_'));

                        var identifier = absoluteGraphIndex + ". " + name;
                        var dotPath = Path.Combine(flowgraphBaseDirectory, $"{identifier}.dot");
                        File.WriteAllText(dotPath, "digraph FlowGraph {\n" + graph);

                        var itemViewModel = new FlowgraphItemViewModel(this, SettingsViewModel, graphIndex.ToString(), name, dotPath, string.Empty);
                        FlowgraphPhases.Add(itemViewModel);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            Output = ex.Message;
        }
        catch (Exception ex)
        {
            Output = ex.ToString();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string PreprocessOutput(string output)
    {
        if (SettingsViewModel.JitDumpInsteadOfDisasm || SettingsViewModel.PrintInlinees)
            return output;

        return DisassemblyPrettifier.Prettify(output, minimalComments: !SettingsViewModel.ShowAsmComments, isInRunMode: SettingsViewModel.RunAppMode);
    }

    private UnconfiguredProject GetUnconfiguredProject(Project project)
    {
        var context = project as IVsBrowseObjectContext;
        if (context is null && project is not null)
            context = project.Object as IVsBrowseObjectContext;

        return context?.UnconfiguredProject;
    }

    private (string, bool) GetPathToRuntimePack()
    {
        var arch = SettingsViewModel.Arch;
        var (_, success) = GetPathToCoreClrChecked();
        if (!success)
            return (null, false);

        var runtimePacksPath = Path.Combine(SettingsViewModel.PathToLocalCoreClr, @"artifacts\bin\runtime");
        string runtimePackPath = null;
        if (Directory.Exists(runtimePacksPath))
        {
            var packs = Directory.GetDirectories(runtimePacksPath, "*-windows-Release-" + arch);
            runtimePackPath = packs.OrderByDescending(i => i).FirstOrDefault();
        }

        if (!Directory.Exists(runtimePackPath))
        {
            Output = "Please, build a runtime-pack in your local repo:\n\n" +
                     $"Run 'build.cmd Clr+Clr.Aot+Libs -c Release -a {arch}' in the repo root\n" + 
                     "Don't worry, you won't have to re-build it every time you change something in jit, vm or corelib.";

            return (null, false);
        }

        return (runtimePackPath, true);
    }

    private (string, bool) GetPathToCoreClrChecked()
    {
        var arch = SettingsViewModel.Arch;
        var clrCheckedFilesDirectory = FindJitDirectory(SettingsViewModel.PathToLocalCoreClr, arch);
        if (string.IsNullOrWhiteSpace(clrCheckedFilesDirectory))
        {
            Output = $"Path to a local dotnet/runtime repository is either not set or it's not built for {arch} arch yet" +
                     (SettingsViewModel.CrossgenIsSelected ? "\n(When you use crossgen and target e.g. arm64 you need coreclr built for that arch)" : "") +
                     "\nPlease clone it and build it in `Checked` mode, e.g.:\n\n" +
                     "git clone git@github.com:dotnet/runtime.git\n" +
                     "cd runtime\n" +
                     $"build.cmd Clr+Clr.Aot+Libs -c Release -rc Checked -a {arch}\n\n";

            return (null, false);
        }

        return (clrCheckedFilesDirectory, true);
    }


    private (string, bool) GetPathToCoreClrCheckedForNativeAot()
    {
        var arch = SettingsViewModel.Arch;
        var releaseFolder = Path.Combine(SettingsViewModel.PathToLocalCoreClr, "artifacts", "bin", "coreclr", $"windows.{arch}.Checked");
        if (!Directory.Exists(releaseFolder) || !Directory.Exists(Path.Combine(releaseFolder, "aotsdk")) || !Directory.Exists(Path.Combine(releaseFolder, "ilc")))
        {
            Output = $"Path to a local dotnet/runtime repository is either not set or it's not correctly built for {arch} arch yet for NativeAOT" +
                     "\nPlease clone it and build it using the following steps.:\n\n" +
                     "git clone git@github.com:dotnet/runtime.git\n" +
                     "cd runtime\n" +
                     $"build.cmd Clr+Clr.Aot+Libs -c Release -rc Checked -a {arch}\n\n";

            return (null, false);
        }

        return (releaseFolder, true);
    }

    public async Task RunOperationAsync(ISymbol symbol, CAProject project)
    {
        var stopwatch = Stopwatch.StartNew();
        var dte = IdeUtils.DTE();

        // It's possible that the last modified C# document is not active (e.g. Disasmo itself is in the focus)
        // So we have no choice but to run Save() for all the opened documents
        dte.SaveAllDocuments();

        try
        {
            IsLoading = true;
            FlowgraphPngPath = null;
            MainPageRequested?.Invoke();
            Success = false;
            _currentSymbol = symbol;
            _currentProject = project;
            Output = "";

            if (symbol is null)
            {
                Output = "Symbol is not recognized, put cursor on a function/class name";
                return;
            }    
            
            string clrCheckedFilesDirectory = null;
            if (SettingsViewModel.UseCustomRuntime)
            {
                var (dir, success) = GetPathToCoreClrChecked();
                if (!success)
                    return;

                clrCheckedFilesDirectory = dir;
            }

            if (symbol is IMethodSymbol { IsGenericMethod: true } && !SettingsViewModel.RunAppMode)
            {
                // TODO: Ask user to specify type parameters
                Output = "Generic methods are only supported in 'Run' mode";
                return;
            }

            ThrowIfCanceled();

            // Find Release-{SettingsViewModel.Arch} configuration:
            var currentProject = dte.GetActiveProject(project.FilePath);
            if (currentProject is null)
            {
                Output = "There no active project. Please re-open solution.";
                return;
            }

            await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync();
            _currentProjectPath = currentProject.FileName;

            var unconfiguredProject = GetUnconfiguredProject(currentProject);
            // Find all configurations, ordered by version descending
            var projectConfigurations = (await IdeUtils.GetProjectConfigurationsAsync(unconfiguredProject))
                .OrderByDescending(IdeUtils.GetTargetFrameworkVersionDimension)
                .AsEnumerable();

            // Filter Release configurations
            var releaseConfigurations = projectConfigurations
                .Where(cfg => string.Equals(IdeUtils.GetConfigurationDimension(cfg), "Release", StringComparison.OrdinalIgnoreCase));

            // Use Release configurations only if we have any
            if (releaseConfigurations.Any())
            {
                projectConfigurations = releaseConfigurations;
            }

            ProjectConfiguration projectConfiguration;
            if (string.IsNullOrWhiteSpace(SettingsViewModel.OverridenTargetFramework))
            {
                // Choose first (highest)
                projectConfiguration = projectConfigurations.FirstOrDefault();
                // Resolve later
                _currentTargetFramework = null;
            }
            else
            {
                // No validation in this case
                _currentTargetFramework = SettingsViewModel.OverridenTargetFramework.Trim();
                var currentTfmVersion = TfmVersion.Parse(_currentTargetFramework);

                // Find the best suitable project configuration
                projectConfiguration = projectConfigurations
                    .FirstOrDefault(cfg => currentTfmVersion is not null && currentTfmVersion.CompareTo(IdeUtils.GetTargetFrameworkVersionDimension(cfg)) >= 0)
                    ?? projectConfigurations.FirstOrDefault();
            }

            var projectProperties = await IdeUtils.GetProjectPropertiesAsync(unconfiguredProject, projectConfiguration);
            ThrowIfCanceled();

            // Resolve target framework
            if (_currentTargetFramework is null)
            {
                int? major;
                if (projectProperties is not null)
                {
                    _currentTargetFramework = await projectProperties.GetEvaluatedPropertyValueAsync("TargetFramework");
                    major = TfmVersion.Parse(_currentTargetFramework)?.Major;
                }
                else
                {
                    // Fallback to net 7.0
                    _currentTargetFramework = "net7.0";
                    major = 7;
                }

                ThrowIfCanceled();

                if (major >= 6)
                {
                    if (!SettingsViewModel.UseCustomRuntime && major < 7)
                    {
                        Output = 
                            "Only net7.0 (and newer) apps are supported with non-locally built dotnet/runtime.\n" + 
                            "Make sure <TargetFramework>net7.0</TargetFramework> is set in your csproj.";

                        return;
                    }
                }
                else
                {
                    Output =
                        "Only net6.0 (and newer) apps are supported.\n" + 
                        "Make sure <TargetFramework>net6.0</TargetFramework> is set in your csproj.";

                    return;
                }
            }

            ThrowIfCanceled();

            if (SettingsViewModel.RunAppMode && SettingsViewModel.UseDotnetPublishForReload)
            {
                // TODO: Fix this
                Output = "\"Run current app\" mode only works with \"dotnet build\" reload strategy, see Options tab.";
                return;
            }

            // Validation for Flowgraph tab
            if (SettingsViewModel.FlowgraphEnable)
            {
                if (string.IsNullOrWhiteSpace(SettingsViewModel.GraphvisDotPath) ||
                    !File.Exists(SettingsViewModel.GraphvisDotPath))
                {
                    Output = 
                        "Graphvis is not installed or path to dot.exe is incorrect, see 'Settings' tab.\n" + 
                        "Graphvis can be installed from https://graphviz.org/download/";

                    return;
                }

                if (!SettingsViewModel.JitDumpInsteadOfDisasm)
                {
                    Output = "Either disable flowgraphs in the 'Flowgraph' tab or enable JitDump.";
                    return;
                }
            }

            if (SettingsViewModel.CrossgenIsSelected || SettingsViewModel.NativeAotIsSelected)
            {
                if (SettingsViewModel.UsePGO)
                {
                    Output = "PGO has no effect on R2R'd/NativeAOT code.";
                    return;
                }

                if (SettingsViewModel.RunAppMode)
                {
                    Output = "Run mode is not supported for crossgen/NativeAOT";
                    return;
                }

                if (SettingsViewModel.UseTieredJit)
                {
                    Output = "TieredJIT has no effect on R2R'd/NativeAOT code.";
                    return;
                }

                if (SettingsViewModel.FlowgraphEnable)
                {
                    Output = "Flowgraphs are not tested with crossgen2/NativeAOT yet (in Disasmo)";
                    return;
                }
            }

            var outputDirectory = projectProperties is null ? "bin" : await projectProperties.GetEvaluatedPropertyValueAsync("OutputPath");
            DisasmoOutputDirectory = Path.Combine(outputDirectory, DisasmoFolder + (SettingsViewModel.UseDotnetPublishForReload ? "_published" : ""));
            var currentProjectDirPath = Path.GetDirectoryName(_currentProjectPath);

            if (SettingsViewModel.IsNonCustomDotnetAotMode())
            {
                ThrowIfCanceled();
                var symbolInfo = SymbolUtils.FromSymbol(_currentSymbol);
                await RunFinalExeAsync(symbolInfo, projectProperties);
                return;
            }

            var targetFrameworkPart = SettingsViewModel.DontGuessTargetFramework && string.IsNullOrWhiteSpace(SettingsViewModel.OverridenTargetFramework) 
                ? "" 
                : $"-f {_currentTargetFramework}";

            // Some things can't be set in CLI e.g. appending to DefineConstants
            var tempProperties = Path.GetTempFileName() + ".props";
            File.WriteAllText(tempProperties, $"""
                                         <?xml version="1.0" encoding="utf-8"?>
                                         <Project>
                                             <PropertyGroup>
                                                 <DefineConstants>$(DefineConstants);DISASMO</DefineConstants>
                                             </PropertyGroup>
                                         </Project>
                                         """);

            ProcessResult publishResult;
            if (SettingsViewModel.UseDotnetPublishForReload)
            {
                LoadingStatus = $"dotnet publish -r win-{SettingsViewModel.Arch} -c Release -o ...";

                var dotnetPublishArgs = $"publish {targetFrameworkPart} -r win-{SettingsViewModel.Arch} -c Release -o {DisasmoOutputDirectory} --self-contained true /p:PublishTrimmed=false /p:PublishSingleFile=false /p:CustomBeforeDirectoryBuildProps=\"{tempProperties}\" /p:WarningLevel=0 /p:TreatWarningsAsErrors=false -v:q";

                publishResult = await ProcessUtils.RunProcess("dotnet", dotnetPublishArgs, null, currentProjectDirPath, cancellationToken: UserCancellationToken);
            }
            else
            {
                if (SettingsViewModel.UseCustomRuntime)
                {
                    var (_, rpSuccess) = GetPathToRuntimePack();
                    if (!rpSuccess)
                        return;
                }

                LoadingStatus = "dotnet build -c Release -o ...";

                var dotnetBuildArgs = $"build {targetFrameworkPart} -c Release -o {DisasmoOutputDirectory} --no-self-contained " +
                                         "/p:RuntimeIdentifier=\"\" " +
                                         "/p:RuntimeIdentifiers=\"\" " +
                                         "/p:WarningLevel=0 " +
                                         $"/p:CustomBeforeDirectoryBuildProps=\"{tempProperties}\" " +
                                         $"/p:TreatWarningsAsErrors=false \"{_currentProjectPath}\"";

                var fasterBuildEnvVars = new Dictionary<string, string>
                {
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
                };

                if (SettingsViewModel.UseNoRestoreFlag)
                {
                    dotnetBuildArgs += " --no-restore --no-dependencies --nologo";
                    fasterBuildEnvVars["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                }

                publishResult = await ProcessUtils.RunProcess(
                    "dotnet", 
                    dotnetBuildArgs, 
                    fasterBuildEnvVars,
                    currentProjectDirPath,
                    cancellationToken: UserCancellationToken);
            }

            File.Delete(tempProperties);
            ThrowIfCanceled();

            if (!string.IsNullOrEmpty(publishResult.Error))
            {
                Output = publishResult.Error;
                return;
            }

            // In case if there are compilation errors:
            if (publishResult.Output.Contains(": error"))
            {
                Output = publishResult.Output;
                return;
            }

            if (SettingsViewModel.UseDotnetPublishForReload && SettingsViewModel.UseCustomRuntime)
            {
                LoadingStatus = "Copying files from locally built CoreCLR";

                var destinationFolder = DisasmoOutputDirectory;
                if (!Path.IsPathRooted(destinationFolder))
                {
                    destinationFolder = Path.Combine(currentProjectDirPath, destinationFolder);
                }

                if (!Directory.Exists(destinationFolder))
                {
                    Output = $"Something went wrong, {destinationFolder} doesn't exist after 'dotnet publish -r win-{SettingsViewModel.Arch} -c Release' step";
                    return;
                }

                var copyClrFilesResult = await ProcessUtils.RunProcess("robocopy", $"/e \"{clrCheckedFilesDirectory}\" \"{destinationFolder}", null, cancellationToken: UserCancellationToken);

                if (!string.IsNullOrEmpty(copyClrFilesResult.Error))
                {
                    Output = copyClrFilesResult.Error;
                    return;
                }
            }

            ThrowIfCanceled();
            var finalSymbolInfo = SymbolUtils.FromSymbol(_currentSymbol);
            await RunFinalExeAsync(finalSymbolInfo, projectProperties);
        }
        catch (OperationCanceledException ex)
        {
            Output = ex.Message;
        }
        catch (Exception ex)
        {
            Output = ex.ToString();
        }
        finally
        {
            IsLoading = false;
            stopwatch.Stop();
            StopwatchStatus = $"Disasm took {stopwatch.Elapsed.TotalSeconds:F1}s";
        }
    }

    public bool CanLoadUninitializedPhase => MaxCountOfLoadingPhases > _countOfLoadingPhases;

    private void TryLoadSelectedPhaseImage()
    {
        if (!_selectedPhase.IsInitialized)
        {
            if (CanLoadUninitializedPhase)
            {
                _ = _selectedPhase.LoadImageAsync(UserCancellationToken);
            }
            else
            {
                UpdatePhaseLoadingStatus(hasReachedTheLimit: true);
                return;
            }
        }

        UpdatePhaseLoadingStatus();
    }

    public void NotifyFlowgraphPhaseLoadingStarted()
    {
        Interlocked.Increment(ref _countOfLoadingPhases);
        UpdatePhaseLoadingStatus();
    }

    public void NotifyFlowgraphPhaseLoadingFinished()
    {
        Interlocked.Decrement(ref _countOfLoadingPhases);
        UpdatePhaseLoadingStatus();
        TryLoadSelectedPhaseImage(); // In case selected phase image load was abandoned due to the limit
    }

    private void UpdatePhaseLoadingStatus(bool hasReachedTheLimit = false)
    {
        IsPhasesLoading = !_selectedPhase.IsInitialized;

        if (!IsPhasesLoading)
            return;

        if (!_selectedPhase.IsBusy && hasReachedTheLimit)
        {
            PhaseLoadingPhrase = 
                $"Cannot load this phase, because the limit" + "\n" + 
                $"in {MaxCountOfLoadingPhases} simultaneously loaded phases" + "\n" + 
                $"has been reached." + "\n\n" +
                $"Wait for the previous phases to load.";

            return;
        }

        PhaseLoadingPhrase = $"Loading...\nQueue: {_countOfLoadingPhases} of {MaxCountOfLoadingPhases} phases";
    }

    private static string FindJitDirectory(string basePath, string arch)
    {
        var jitDirectory = Path.Combine(basePath, $@"artifacts\bin\coreclr\windows.{arch}.Checked");
        if (Directory.Exists(jitDirectory))
            return jitDirectory;

        jitDirectory = Path.Combine(basePath, $@"artifacts\bin\coreclr\windows.{arch}.Debug");
        if (Directory.Exists(jitDirectory))
            return jitDirectory;

        return null;
    }
}