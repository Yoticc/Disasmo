using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Editor.Commanding;
using Microsoft.VisualStudio.ExtensionManager;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Disasmo;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideBindingPath]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(DisasmWindow))]
public sealed class DisasmoPackage : AsyncPackage
{
    public const string PackageGuidString = "6d23b8d8-92f1-4f92-947a-b9021f6ab3dc";
    public const string PackageProductIdString = "Disasmo.39513ef5-c3ee-4547-b7be-f29c752b591d";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        try
        {
            Current = this;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var disasmoCommand = IdeUtils.DTE().Commands.Item("Tools.Disasmo", 0);
            if (disasmoCommand is null)
                return;

            var binding = "";
            if (disasmoCommand.Bindings is object[] bindingArray)
            {
                var hotkeys = bindingArray.Select(b => b.ToString()).ToArray();
                // Prefer Text Editor over Global
                var bindingPair = hotkeys.FirstOrDefault(h => h.StartsWith("Text Editor::")) ?? hotkeys.FirstOrDefault();
                if (bindingPair is not null && bindingPair.Contains("::"))
                {
                    binding = bindingPair.Substring(bindingPair.IndexOf("::", StringComparison.Ordinal) + 2);
                }
            }
            else
            {
                if (disasmoCommand.Bindings is string bindingString && bindingString.Contains("::"))
                {
                    binding = bindingString.Substring(bindingString.IndexOf("::", StringComparison.Ordinal) + 2);
                }
            }
            HotKey = binding;
        }
        catch
        {
        }
    }

    public static string HotKey = "";

    public static DisasmoPackage Current { get; set; }

    public async Task<Version> GetLatestVersionOnlineAsync()
    {
        try
        {
            // Question: Is there an API to do it?
            // Answer:
            // Visual Studio has its own functionality for fetching current package version.
            // For example: Microsoft.VisualStudio.Setup.Services.MarketplaceExtensionService (internal)
            //              NuGet.PackageManagement.UI.Utility.NuGetSearchServiceReconnector (internal)
            //              Microsoft.VisualStudio.ExtensionManager.Implementation.ExtensionRepositoryService
            //               (public, BUT vssdk.extensionmanager DOES NOT PROVIDE GetSearchQueryExtensions METHOD FOR IVsExtensionRepository)
            // Therefore, the easiest method is to directly request the website.

            using var client = new HttpClient();
            var content = await client.GetStringAsync("https://marketplace.visualstudio.com/items?itemName=EgorBogatov.Disasmo");
            var marker = "extensions/egorbogatov/disasmo/";
            var index = content.IndexOf(marker);
            return Version.Parse(content.Substring(index + marker.Length, content.IndexOf('/', index + marker.Length) - index - marker.Length));
        }
        catch 
        {
            return new Version(0, 0); 
        }
    }

    public Version GetCurrentVersion()
    {
        try
        {
            var extensionManager = GetService(typeof(SVsExtensionManager)) as IVsExtensionManager;
            var ourExtension = extensionManager.GetInstalledExtension(PackageProductIdString);
            var currentVersion = ourExtension.Header.Version;
            return currentVersion;
        }
        catch
        {
            return new Version(0, 0); 
        }
    }
}

public class DisasmoCommandBinding
{
    private const int DisasmoCommandId = 0x0100;
    private const int DisasmoWindowCommandId = 0x0200;
    private const string DisasmoCommandSet = "4fd0ea18-9f33-43da-ace0-e387656e584c";

    [Export]
    [CommandBinding(DisasmoCommandSet, DisasmoCommandId, typeof(DisasmoCommandArgs))]
    internal CommandBindingDefinition disasmoCommandBinding;

    [Export]
    [CommandBinding(DisasmoCommandSet, DisasmoWindowCommandId, typeof(DisasmoWindowCommandArgs))]
    internal CommandBindingDefinition disasmoWindowCommandBinding;
}

public class DisasmoCommandArgs(ITextView textView, ITextBuffer textBuffer) : EditorCommandArgs(textView, textBuffer);

[Export(typeof(ICommandHandler))]
[ContentType("text")]
[Name(nameof(DisasmoCommandHandler))]
public class DisasmoCommandHandler : ICommandHandler<DisasmoCommandArgs>
{
    public string DisplayName => "Disasmo this";

    public CommandState GetCommandState(DisasmoCommandArgs args) => CommandState.Available;

    public int GetCaretPosition(ITextView view)
    {
        try
        {
            return view?.Caret?.Position.BufferPosition ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    public bool ExecuteCommand(DisasmoCommandArgs args, CommandExecutionContext context)
    {
        // Save changes made to the active document before we start disasm
        IdeUtils.DTE().SaveActiveDocument();

        var document = args.TextView?.TextBuffer?.GetRelatedDocuments()?.FirstOrDefault();
        if (document is null)
            return true;

        var position = GetCaretPosition(args.TextView);
        if (position == -1)
            return true;

        ThreadPool.QueueUserWorkItem(CallBack);

        return true;

        async void CallBack(object _)
        {
            try
            {
                if (DisasmoPackage.Current is null)
                {
                    MessageBox.Show("Disasmo is still loading... (sometimes it takes a while for add-ins to fully load - it makes VS faster to start).");
                    return;
                }

                await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync();

                var symbol = await DisasmMethodOrClassAction.GetSymbolStaticAsync(document, position, default, true);
                var window = await IdeUtils.ShowWindowAsync<DisasmWindow>(true, default);
                if (window?.ViewModel is { } viewModel)
                {
                    viewModel.RunOperationAsync(symbol, document.Project);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}

public class DisasmoWindowCommandArgs(ITextView textView, ITextBuffer textBuffer) : EditorCommandArgs(textView, textBuffer);

[Export(typeof(ICommandHandler))]
[ContentType("text")]
[Name(nameof(DisasmoWindowCommandHandler))]
public class DisasmoWindowCommandHandler : ICommandHandler<DisasmoWindowCommandArgs>
{
    public string DisplayName => "Open DisasmWindow";

    public CommandState GetCommandState(DisasmoWindowCommandArgs args) => CommandState.Available;

    public bool ExecuteCommand(DisasmoWindowCommandArgs args, CommandExecutionContext context)
    {
        ThreadPool.QueueUserWorkItem(CallBack);

        return true;

        async void CallBack(object _)
        {
            try
            {
                if (DisasmoPackage.Current is null)
                {
                    MessageBox.Show("Disasmo is still loading... (sometimes it takes a while for add-ins to fully load - it makes VS faster to start).");
                    return;
                }

                await DisasmoPackage.Current.JoinableTaskFactory.SwitchToMainThreadAsync();
                await IdeUtils.ShowWindowAsync<DisasmWindow>(true, default);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}