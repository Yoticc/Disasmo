using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Editor.Commanding;
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

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        try
        {
            Current = this;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var disasmoCommand = IdeUtils.DTE().Commands.Item("Tools.Disasmo", 0);
            if (disasmoCommand is null)
                return;

            var binding = string.Empty;
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

    public static string HotKey = string.Empty;

    public static DisasmoPackage Current { get; set; }

    public static async Task<Version> GetLatestVersionOnlineAsync()
    {
        try
        {
            await Task.Delay(3000);

            // Is there an API to do it?
            using var client = new HttpClient();
            var content = await client.GetStringAsync("https://marketplace.visualstudio.com/items?itemName=EgorBogatov.Disasmo");
            var marker = "extensions/egorbogatov/disasmo/";
            var index = content.IndexOf(marker);
            return Version.Parse(content.Substring(index + marker.Length, content.IndexOf('/', index + marker.Length) - index - marker.Length));
        }
        catch { return new Version(0, 0); }
    }

    public Version GetCurrentVersion()
    {
        //TODO: Fix
        return new Version(5, 9, 2);

        //try
        //{
        //    // get ExtensionManager
        //    IVsExtensionManager manager = GetService(typeof(SVsExtensionManager)) as IVsExtensionManager;
        //    // get your extension by Product Id
        //    IInstalledExtension myExtension = manager.GetInstalledExtension("Disasmo.39513ef5-c3ee-4547-b7be-f29c752b591d");
        //    // get current version
        //    Version currentVersion = myExtension.Header.Version;
        //    return currentVersion;
        //}
        //catch {return new Version(0, 0); }
    }
}

public class DisasmoCommandArgs : EditorCommandArgs
{
    public DisasmoCommandArgs(ITextView textView, ITextBuffer textBuffer)
        : base(textView, textBuffer)
    {
    }
}

public class DisasmoCommandBinding
{
    private const int DisasmoCommandId = 0x0100;
    private const string DisasmoCommandSet = "4fd0ea18-9f33-43da-ace0-e387656e584c";

    [Export]
    [CommandBinding(DisasmoCommandSet, DisasmoCommandId, typeof(DisasmoCommandArgs))]
    internal CommandBindingDefinition disasmoCommandBinding;
}

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