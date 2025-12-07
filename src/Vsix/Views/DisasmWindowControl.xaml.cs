using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Xml;

namespace Disasmo;

/// <summary>
/// Interaction logic for DisasmWindowControl.
/// </summary>
public partial class DisasmWindowControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DisasmWindowControl"/> class.
    /// </summary>
    public DisasmWindowControl()
    {
        InitializeComponent();

        // Taken from https://gist.github.com/madskristensen/4d205244dd92c37c82e7
        // It is necessary because usual component system... does not work for some reason.
        // If you have enough experience, you can try to find out why this happens and then remove this crutch.
        var compositionService = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
        compositionService.DefaultCompositionService.SatisfyImportsOnce(this);

        LoadAsmSyntaxDefinition();
        VSColorTheme.ThemeChanged += e => OnThemeUpdated();

        MainViewModel.PropertyChanged += (_, e) =>
        {
            // AvalonEdit is not bindable (lazy workaround)
            if (e.PropertyName == "Output") OutputEditor.Text = MainViewModel.Output;
            if (e.PropertyName == "PreviousOutput") OutputEditorPrev.Text = MainViewModel.PreviousOutput;
            if (e.PropertyName == "Success") ApplySyntaxHighlighting(asm: MainViewModel.IsAsmContextInEditor);
        };

        MainViewModel.MainPageRequested += () =>
        {
            if (TabControl.SelectedIndex != 2) // Ugly fix: Don't leave "flowgraph" tab on reload
                TabControl.SelectedIndex = 0;
        };
    }

    [Import]
    private IClassificationFormatMapService classificationFormatMapService { get; set; }

    [Import]
    private IClassificationTypeRegistryService classificationTypeRegistryService { get; set; }

    private IHighlightingDefinition _asmSyntaxHighlighting;
    private IHighlightingDefinition _txtSyntaxHighlighting;

    private IHighlightingDefinition asmSyntaxHighlighting
        => _asmSyntaxHighlighting ??= GetAsmSyntaxHighlighting();

    private IHighlightingDefinition txtSyntaxHighlighting
        => _txtSyntaxHighlighting ??= (IHighlightingDefinition)new HighlightingDefinitionTypeConverter().ConvertFrom("txt");

    private XshdSyntaxDefinition asmSyntaxDefinition;

    private void LoadAsmSyntaxDefinition()
    {
        using var stream = typeof(DisasmWindowControl).Assembly.GetManifestResourceStream("Disasmo.Resources.AsmSyntax.xshd");
        using var reader = new XmlTextReader(stream);
        asmSyntaxDefinition = HighlightingLoader.LoadXshd(reader);
    }

    private void ApplySyntaxHighlighting(bool asm)
    {
        if (asm)
            ApplySyntaxHighlighting(asmSyntaxHighlighting);
        else ApplySyntaxHighlighting(txtSyntaxHighlighting);
    }

    private void ApplySyntaxHighlighting(IHighlightingDefinition syntaxHighlighting)
    {
        OutputEditor.SyntaxHighlighting = syntaxHighlighting;
        OutputEditorPrev.SyntaxHighlighting = syntaxHighlighting;
    }

    private IHighlightingDefinition GetAsmSyntaxHighlighting()
    {
        var syntaxDefinition = asmSyntaxDefinition;
        
        foreach (var element in syntaxDefinition.Elements)
        {
            if (element is not XshdColor elementColor)
                continue;
            
            switch (elementColor.Name)
            {
                case "Keywords":
                    elementColor.Foreground = LoadBrushFromResources("KeywordColor");
                    break;
                case "Comment":
                    elementColor.Foreground = LoadBrushFromResources("CommentColor");
                    break;
                case "Char":
                    elementColor.Foreground = LoadBrushFromResources("CharColor");
                    break;
            }
        }

        var syntaxHighlighting = HighlightingLoader.Load(syntaxDefinition, HighlightingManager.Instance);
        return syntaxHighlighting;

        SimpleHighlightingBrush LoadBrushFromResources(string resourceKey)
        {
            var resource = Resources[resourceKey] as SolidColorBrush;
            var color = resource.Color;
            var brush = new SimpleHighlightingBrush(color);
            return brush;
        }
    }

    private void UpdateAsmSyntaxHighlighting()
    {
        _asmSyntaxHighlighting = GetAsmSyntaxHighlighting();

        if (MainViewModel.IsAsmContextInEditor)
            ApplySyntaxHighlighting(asm: true);
    }

    private void OnThemeUpdated()
    {
        Resources["PlainText"] = GetThemedBrush(TreeViewColors.HighlightedSpanTextBrushKey);
        Resources["FileTabInactiveBorderBrush"] = GetThemedBrush(EnvironmentColors.FileTabInactiveBorderBrushKey);
        Resources["DarkGrayTextBrush"] = GetThemedBrush(CommonControlsColors.TextBoxTextDisabledBrushKey);
        Resources["CheckBoxBorder"] = GetThemedBrush(CommonControlsColors.CheckBoxBorderBrushKey);
        Resources["CheckBoxBackground"] = GetThemedBrush(CommonControlsColors.CheckBoxBackgroundBrushKey);
        Resources["CheckBoxHoveredBackground"] = GetThemedBrush(CommonControlsColors.CheckBoxBackgroundHoverBrushKey);

        var textFormatMap = classificationFormatMapService.GetClassificationFormatMap("text");
        Resources["KeywordColor"] = GetEditorBrushFromClassification(textFormatMap, "keyword");
        Resources["CommentColor"] = GetEditorBrushFromClassification(textFormatMap, "comment");
        Resources["CharColor"] = GetEditorBrushFromClassification(textFormatMap, "string");
        Resources["NumberLineBrush"] = GetEditorBrushFromClassification(textFormatMap, "line number");

        UpdateAsmSyntaxHighlighting();

        SolidColorBrush GetEditorBrushFromClassification(IClassificationFormatMap classificationMap, string classificationName)
        {
            var properties = classificationMap.GetTextProperties(classificationTypeRegistryService.GetClassificationType(classificationName));
            return (SolidColorBrush)properties.ForegroundBrush;
        }

        static SolidColorBrush GetThemedBrush(ThemeResourceKey themeResourceKey)
        {
            var drawingColor = VSColorTheme.GetThemedColor(themeResourceKey);
            var mediaColor = ConvertDrawingColorToMediaColor(drawingColor);
            var brush = new SolidColorBrush(mediaColor);
            return brush;

            static System.Windows.Media.Color ConvertDrawingColorToMediaColor(System.Drawing.Color drawingColor)
            {
                var mediaColor = System.Windows.Media.Color.FromArgb(drawingColor.A, drawingColor.R, drawingColor.G, drawingColor.B);
                return mediaColor;
            }
        }
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // Requires for the initial appearance of the first tab item element
        TabControl.SelectedIndex = 0;
        if (TabControl.Items[0] is System.Windows.Controls.TabItem selectedTab)
            selectedTab.Focus();

        OnThemeUpdated();
    }

    // Some custom style properties cannot be applied when the style is specified implicitly.
    // Therefore, there are two ways: 
    //  1. Specify the style for each element explicitly.
    //  2. Use a loading event handler for each element and apply the overridden properties in it.
    // The second option was chosen for convenience.
    private void CheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        var checkBox = sender as System.Windows.Controls.CheckBox;
        checkBox.Padding = new Thickness(3, 2, 3, 0);
    }

    private void TextBox_Loaded(object sender, RoutedEventArgs e)
    {
        var checkBox = sender as System.Windows.Controls.TextBox;
        checkBox.Padding = new Thickness(0, 2, 0, 0);
    }

    private void Button_Loaded(object sender, RoutedEventArgs e)
    {
        var checkBox = sender as System.Windows.Controls.Button;
        checkBox.Padding = new Thickness(0, 0, 0, 0);
    }

    // The issue is that the IsFocused property is only set after the tab item's trigger is called, so the trigger thinks the item is not selected.
    private void TabItem_PreviewMouseLeftButtonDown_FixDoubleTabIssue(object sender, MouseButtonEventArgs e)
    {
        var tabItem = sender as System.Windows.Controls.TabItem;
        tabItem.Focus();
    }

    private void Hyperlink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        var processStartInfo = new ProcessStartInfo(e.Uri.AbsoluteUri);
        Process.Start(processStartInfo);
        e.Handled = true;
    }

    private void OnOpenLogs(object s, RequestNavigateEventArgs e)
    {
        try
        {
            IdeUtils.DTE().ItemOperations.OpenFile(UserLogger.LogFile);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnOpenReleaseNotes(object s, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(e.Uri.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnClearLogs(object s, RequestNavigateEventArgs e)
    {
        try
        {
            File.WriteAllText(UserLogger.LogFile, "");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async void OnOpenFolderWithFlowGraphs(object sender, RequestNavigateEventArgs e)
    {
        const string AbsoluteFilePathLabel = "file:///";

        var uri = e.Uri;
        if (uri is null)
            return;

        var file = uri.ToString();
        if (file is null)
            return;

        if (file.StartsWith(AbsoluteFilePathLabel))
            file = file.Substring(AbsoluteFilePathLabel.Length);

        if (file.Length == 0)
            return;

        try
        {
            await ProcessUtils.RunProcess("explorer.exe", Path.GetDirectoryName(file));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

	private void AvalonEdit_MouseWheel(object sender, MouseWheelEventArgs e)
	{
        if ((Control.ModifierKeys & Keys.Control) != Keys.Control)
            return;
         
        var fontSize = MainViewModel.SettingsViewModel.FontSize;
        fontSize += e.Delta * SystemInformation.MouseWheelScrollLines / 120;

        if (fontSize < 8)
            fontSize = 8;
        else if (fontSize > 50)
            fontSize = 50;
            
        MainViewModel.SettingsViewModel.FontSize = fontSize;

        e.Handled = true;
    }
}