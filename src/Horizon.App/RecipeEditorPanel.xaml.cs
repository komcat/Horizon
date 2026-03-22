using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using Horizon.Core;
using Horizon.Process.Recipe;
using Horizon.Process.Recipe.Ast;

namespace Horizon.App;

public partial class RecipeEditorPanel : UserControl
{
    private readonly IAppContext _context;
    private readonly ObservableCollection<WatchItem> _watchItems = new();
    private CancellationTokenSource? _cts;
    private RecipeContext? _runContext;
    private string? _currentFilePath;
    private bool _isRunning;
    private bool _suppressTextChanged;
    private DispatcherTimer? _highlightTimer;
    private IReadOnlyList<RecipeParseError> _lastErrors = Array.Empty<RecipeParseError>();
    private string _autoCompletePrefix = "";
    private bool _autoCompleteAfterDot;
    private DateTime _lastWatchRefresh = DateTime.MinValue;
    private FileSystemWatcher? _fileWatcher;
    private DateTime _lastSaveTime = DateTime.MinValue;

    private static readonly string[] LanguageKeywords = { "set", "loop", "if", "else", "true", "false", "expr", "system" };

    // Syntax colors (VS Code dark+ inspired)
    private static readonly SolidColorBrush ColorKeyword = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));    // blue
    private static readonly SolidColorBrush ColorString = Freeze(new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78)));     // orange-brown
    private static readonly SolidColorBrush ColorNumber = Freeze(new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8)));     // green
    private static readonly SolidColorBrush ColorComment = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)));    // green
    private static readonly SolidColorBrush ColorDevice = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));     // teal
    private static readonly SolidColorBrush ColorMethod = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)));     // yellow
    private static readonly SolidColorBrush ColorOperator = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));   // white
    private static readonly SolidColorBrush ColorPunctuation = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80))); // gray
    private static readonly SolidColorBrush ColorDefault = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));    // light gray
    private static readonly SolidColorBrush ColorVariable = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)));   // light blue
    private static readonly SolidColorBrush ColorBoolean = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));    // blue
    private static readonly SolidColorBrush ColorError = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47)));      // red
    private static readonly SolidColorBrush ColorErrorBg = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x1D, 0x1D)));    // dark red bg

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static readonly string RecipeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "recipes");

    private static readonly string LastRecipeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Horizon", "recipes", ".lastrecipe");

    public RecipeEditorPanel(IAppContext context)
    {
        InitializeComponent();
        _context = context;
        WatchGrid.ItemsSource = _watchItems;

        // Debounce highlighting to avoid lag while typing
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            ApplySyntaxHighlighting();
        };

        SetEditorText("# New recipe\n");
        LoadLastRecipe();

        // Defer tab title update until we're in the visual tree
        Loaded += (_, _) => UpdateFileDisplay();
    }

    // --- Plain text helpers for RichTextBox ---

    private string GetEditorText()
    {
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        var text = range.Text;
        // RichTextBox appends \r\n, normalize
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private void SetEditorText(string text)
    {
        _suppressTextChanged = true;
        Editor.Document.Blocks.Clear();
        Editor.Document.Blocks.Add(new Paragraph(new Run(text)));
        _suppressTextChanged = false;
        UpdateLineNumbers();
        ApplySyntaxHighlighting();
    }

    // --- Syntax highlighting ---

    private void ApplySyntaxHighlighting()
    {
        var text = GetEditorText();
        if (string.IsNullOrEmpty(text)) return;

        _suppressTextChanged = true;

        try
        {
            // Save caret position as text-character offset (not symbol offset)
            var caretCharOffset = GetTextCharOffset(Editor.Document.ContentStart, Editor.CaretPosition);
            var verticalOffset = Editor.VerticalOffset;

            // Tokenize
            var lexer = new RecipeLexer(text);
            List<RecipeToken> tokens;
            try { tokens = lexer.Tokenize(); }
            catch { _suppressTextChanged = false; return; }

            // Parse for errors
            var parser = new RecipeParser(tokens);
            parser.Parse();
            _lastErrors = parser.Errors;
            UpdateErrorList();

            // Build a single paragraph with colored runs
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 1 };
            var lines = text.Split('\n');

            // Map each token to a color
            // We'll work character by character per line, coloring by token spans
            var tokenColors = new SolidColorBrush[text.Length];
            for (int i = 0; i < tokenColors.Length; i++)
                tokenColors[i] = ColorDefault;

            // Track device.method patterns for special coloring
            bool expectMethod = false;
            bool expectDeviceDot = false;
            int lastIdentEnd = -1;

            for (int t = 0; t < tokens.Count; t++)
            {
                var tok = tokens[t];
                if (tok.Type == TokenType.EOF || tok.Type == TokenType.Newline) continue;

                // Find token position in text by line/col
                int offset = GetOffset(lines, tok.Line, tok.Column);
                if (offset < 0 || offset >= text.Length) continue;

                int len = tok.Value.Length;
                if (tok.Type == TokenType.StringLiteral) len += 2; // include quotes
                if (tok.Type == TokenType.Comment)
                {
                    // Token column points to '#', value is the trimmed text after '#'
                    // Color from '#' to end of line
                    int lineIdx = tok.Line - 1;
                    if (lineIdx >= 0 && lineIdx < lines.Length)
                        len = lines[lineIdx].Length - (tok.Column - 1);
                    else
                        len = tok.Value.Length + 2;
                }

                // Clamp
                if (offset + len > text.Length) len = text.Length - offset;

                var color = GetTokenColor(tok, expectMethod);

                // Detect device.method pattern
                if (tok.Type == TokenType.Identifier && !expectMethod)
                {
                    // Check if next non-newline token is Dot
                    if (t + 1 < tokens.Count && tokens[t + 1].Type == TokenType.Dot)
                    {
                        color = ColorDevice; // This identifier is a device name
                        expectDeviceDot = true;
                    }
                }
                else if (tok.Type == TokenType.Dot && expectDeviceDot)
                {
                    expectDeviceDot = false;
                    expectMethod = true;
                    color = ColorPunctuation;
                }
                else if (tok.Type == TokenType.Identifier && expectMethod)
                {
                    color = ColorMethod;
                    expectMethod = false;
                }
                else
                {
                    expectMethod = false;
                    expectDeviceDot = false;
                }

                for (int i = offset; i < offset + len && i < tokenColors.Length; i++)
                    tokenColors[i] = color;
            }

            // Mark error lines with red underline in the error background
            var errorLines = new HashSet<int>();
            foreach (var err in _lastErrors)
                errorLines.Add(err.Line);

            // Build runs line by line
            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                int lineStart = GetLineStart(lines, lineIdx);
                var line = lines[lineIdx];
                bool isErrorLine = errorLines.Contains(lineIdx + 1);

                if (line.Length == 0)
                {
                    if (lineIdx < lines.Length - 1)
                        paragraph.Inlines.Add(new Run("\n") { Foreground = ColorDefault });
                    continue;
                }

                // Group consecutive chars with same color into runs
                var currentColor = tokenColors[Math.Min(lineStart, tokenColors.Length - 1)];
                int runStart = 0;

                for (int c = 1; c <= line.Length; c++)
                {
                    var nextColor = c < line.Length
                        ? tokenColors[Math.Min(lineStart + c, tokenColors.Length - 1)]
                        : null;

                    if (nextColor != currentColor || c == line.Length)
                    {
                        var runText = line[runStart..c];
                        var run = new Run(runText) { Foreground = currentColor };
                        if (isErrorLine)
                            run.Background = ColorErrorBg;
                        paragraph.Inlines.Add(run);

                        if (c < line.Length)
                        {
                            currentColor = nextColor!;
                            runStart = c;
                        }
                    }
                }

                // Add newline between lines (not after last)
                if (lineIdx < lines.Length - 1)
                    paragraph.Inlines.Add(new Run("\n") { Foreground = ColorDefault });
            }

            Editor.Document.Blocks.Clear();
            Editor.Document.Blocks.Add(paragraph);

            // Restore caret position from text-character offset
            try
            {
                var restored = GetPointerAtTextCharOffset(Editor.Document.ContentStart, caretCharOffset);
                if (restored != null)
                    Editor.CaretPosition = restored;
                Editor.ScrollToVerticalOffset(verticalOffset);
            }
            catch { /* caret restore best-effort */ }
        }
        catch
        {
            // If highlighting fails, don't crash — just leave text as-is
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private static SolidColorBrush GetTokenColor(RecipeToken tok, bool isMethodPosition)
    {
        return tok.Type switch
        {
            TokenType.KeywordSet or TokenType.KeywordLoop or TokenType.KeywordIf
                or TokenType.KeywordElse or TokenType.KeywordExpr => ColorKeyword,
            TokenType.KeywordTrue or TokenType.KeywordFalse => ColorBoolean,
            TokenType.StringLiteral => ColorString,
            TokenType.NumberLiteral => ColorNumber,
            TokenType.Comment => ColorComment,
            TokenType.Identifier when isMethodPosition => ColorMethod,
            TokenType.Identifier => ColorVariable,
            TokenType.Dot => ColorPunctuation,
            TokenType.LeftParen or TokenType.RightParen or TokenType.LeftBrace
                or TokenType.RightBrace or TokenType.Colon or TokenType.Comma => ColorPunctuation,
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
                or TokenType.Greater or TokenType.Less or TokenType.GreaterEqual
                or TokenType.LessEqual or TokenType.EqualEqual or TokenType.BangEqual
                or TokenType.Equals => ColorOperator,
            _ => ColorDefault,
        };
    }

    private static int GetOffset(string[] lines, int line, int col)
    {
        if (line < 1 || line > lines.Length) return -1;
        int offset = 0;
        for (int i = 0; i < line - 1; i++)
            offset += lines[i].Length + 1; // +1 for \n
        return offset + col - 1;
    }

    private static int GetLineStart(string[] lines, int lineIndex)
    {
        int offset = 0;
        for (int i = 0; i < lineIndex; i++)
            offset += lines[i].Length + 1;
        return offset;
    }

    // --- Error list ---

    private void UpdateErrorList()
    {
        ErrorList.Items.Clear();
        foreach (var err in _lastErrors)
            ErrorList.Items.Add($"Line {err.Line}:{err.Column}  {err.Message}");

        ErrorsTabText.Text = _lastErrors.Count > 0
            ? $"Errors ({_lastErrors.Count})"
            : "Errors (0)";

        ErrorIndicator.Text = _lastErrors.Count > 0
            ? $"\u26A0 {_lastErrors.Count} error(s)"
            : "";

        // Auto-switch to errors tab if there are errors, output tab if clear
        if (_lastErrors.Count > 0 && !_isRunning)
            BottomTabs.SelectedIndex = 1;
    }

    private void ErrorList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ErrorList.SelectedIndex < 0 || ErrorList.SelectedIndex >= _lastErrors.Count) return;
        var err = _lastErrors[ErrorList.SelectedIndex];
        NavigateToLine(err.Line);
    }

    private void NavigateToLine(int line)
    {
        try
        {
            var text = GetEditorText();
            var lines = text.Split('\n');
            if (line < 1 || line > lines.Length) return;

            int charIndex = 0;
            for (int i = 0; i < line - 1; i++)
                charIndex += lines[i].Length + 1;

            // Find the TextPointer at that offset
            var start = Editor.Document.ContentStart;
            var pointer = GetTextPointerAtOffset(start, charIndex);
            var endPointer = GetTextPointerAtOffset(start, charIndex + lines[line - 1].Length);

            if (pointer != null && endPointer != null)
            {
                Editor.Selection.Select(pointer, endPointer);
                Editor.Focus();

                // Scroll into view
                var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                Editor.ScrollToVerticalOffset(Editor.VerticalOffset + rect.Top - Editor.ActualHeight / 3);
            }
        }
        catch { /* ignore navigation errors */ }
    }

    /// <summary>Count text characters between two pointers (ignoring formatting symbols).</summary>
    private static int GetTextCharOffset(TextPointer start, TextPointer end)
    {
        int count = 0;
        var pointer = start;
        while (pointer != null && pointer.CompareTo(end) < 0)
        {
            var ctx = pointer.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                if (next == null) break;
                // Don't count past the end
                int runLength = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (next.CompareTo(end) > 0)
                {
                    // end is within this run
                    var textInRun = new TextRange(pointer, end).Text;
                    count += textInRun.Length;
                    break;
                }
                count += runLength;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return count;
    }

    /// <summary>Find TextPointer at a given text-character offset.</summary>
    private static TextPointer? GetPointerAtTextCharOffset(TextPointer start, int offset)
        => GetTextPointerAtOffset(start, offset);

    private static TextPointer? GetTextPointerAtOffset(TextPointer start, int offset)
    {
        var pointer = start;
        int remaining = offset;

        while (pointer != null && remaining > 0)
        {
            var ctx = pointer.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                int runLength = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (runLength >= remaining)
                    return pointer.GetPositionAtOffset(remaining);
                remaining -= runLength;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        return pointer;
    }

    // --- File display ---

    private void UpdateFileDisplay()
    {
        if (_currentFilePath != null)
        {
            var fileName = Path.GetFileName(_currentFilePath);
            FilePathDisplay.Text = _currentFilePath;
            FilePathDisplay.Foreground = ColorDefault;
            FileNameText.Text = fileName;

            // Update AvalonDock tab title
            var doc = FindParentLayoutDocument();
            if (doc != null)
                doc.Title = $"Recipe — {fileName}";
        }
        else
        {
            FilePathDisplay.Text = "Untitled (not saved)";
            FilePathDisplay.Foreground = ColorPunctuation;
            FileNameText.Text = "";

            var doc = FindParentLayoutDocument();
            if (doc != null)
                doc.Title = "Recipe Editor";
        }
    }

    private LayoutDocument? FindParentLayoutDocument()
    {
        // Walk up the logical/visual tree to find the AvalonDock LayoutDocument
        try
        {
            var parent = this.Parent;
            while (parent != null)
            {
                if (parent is System.Windows.FrameworkElement fe)
                    parent = fe.Parent;
                else
                    break;
            }
            // Alternative: search the dock manager
            var mainWindow = Window.GetWindow(this);
            if (mainWindow == null) return null;
            var dockMgr = mainWindow.FindName("DockManager") as AvalonDock.DockingManager;
            if (dockMgr == null) return null;
            return dockMgr.Layout
                .Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(d => d.Content == this);
        }
        catch { return null; }
    }

    // --- File operations ---

    private void New_Click(object sender, RoutedEventArgs e)
    {
        SetEditorText("# New recipe\n");
        _currentFilePath = null;
        UpdateFileDisplay();
        StatusText.Text = "New recipe";
        OutputList.Items.Clear();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(RecipeDir);
        var dlg = new FilePickerDialog(FilePickerMode.Open, RecipeDir, ".hrcp", "Horizon Recipe")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.SelectedPath != null)
            LoadFile(dlg.SelectedPath);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath != null)
            SaveFile(_currentFilePath);
        else
            SaveAs_Click(sender, e);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(RecipeDir);
        var defaultName = _currentFilePath != null
            ? Path.GetFileNameWithoutExtension(_currentFilePath) : "";
        var dlg = new FilePickerDialog(FilePickerMode.Save, RecipeDir, ".hrcp", "Horizon Recipe", defaultName)
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.SelectedPath != null)
            SaveFile(dlg.SelectedPath);
    }

    private void LoadFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            SetEditorText(text);
            _currentFilePath = path;
            UpdateFileDisplay();
            StatusText.Text = $"Loaded: {Path.GetFileName(path)}";
            SaveLastRecipePath(path);
            WatchFile(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load error: {ex.Message}";
        }
    }

    private void SaveFile(string path)
    {
        try
        {
            _lastSaveTime = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, GetEditorText());
            _currentFilePath = path;
            UpdateFileDisplay();
            StatusText.Text = $"Saved: {Path.GetFileName(path)}";
            SaveLastRecipePath(path);
            WatchFile(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save error: {ex.Message}";
        }
    }

    private void WatchFile(string path)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (dir == null || name == null) return;

        _fileWatcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _fileWatcher.Changed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Skip if we just saved (within 2 seconds)
        if ((DateTime.UtcNow - _lastSaveTime).TotalSeconds < 2) return;

        // Brief delay to let the write finish
        Thread.Sleep(100);

        Dispatcher.BeginInvoke(() =>
        {
            if (_isRunning) return; // don't reload while executing
            if (_currentFilePath == null || !File.Exists(_currentFilePath)) return;

            try
            {
                var text = File.ReadAllText(_currentFilePath);
                var current = GetEditorText();
                if (text.Replace("\r\n", "\n") == current.Replace("\r\n", "\n")) return;

                SetEditorText(text);
                StatusText.Text = $"Reloaded: {Path.GetFileName(_currentFilePath)}";
            }
            catch { /* file may be locked briefly */ }
        });
    }

    private void LoadLastRecipe()
    {
        try
        {
            if (File.Exists(LastRecipeFile))
            {
                var path = File.ReadAllText(LastRecipeFile).Trim();
                if (File.Exists(path))
                    LoadFile(path);
            }
        }
        catch { /* ignore */ }
    }

    private static void SaveLastRecipePath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastRecipeFile)!);
            File.WriteAllText(LastRecipeFile, path);
        }
        catch { /* ignore */ }
    }

    // --- Execution ---

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var engine = _context.RecipeEngine;

        if (!engine.TryAcquire("RecipeEditor"))
        {
            StatusText.Text = $"Engine busy — {engine.RunningOwner} is running";
            return;
        }

        try
        {
            SetRunning(true);
            OutputList.Items.Clear();
            _watchItems.Clear();
            BottomTabs.SelectedIndex = 0; // Switch to Output tab

            // Parse
            var (program, errors) = RecipeEngine.Parse(GetEditorText());
            if (program == null || errors.Count > 0)
            {
                foreach (var err in errors)
                    AddOutput($"[ERR] Line {err.Line}:{err.Column} — {err.Message}");
                StatusText.Text = $"Parse failed: {errors.Count} error(s)";
                BottomTabs.SelectedIndex = 1; // Switch to Errors tab
                return;
            }

            // Setup context
            _cts = new CancellationTokenSource();
            _runContext = new RecipeContext();
            engine.StepByStepMode = StepModeCheck.IsChecked == true;

            // Wire callbacks
            engine.OnStepStarted = (line, device, method) =>
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"Line {line}: {device}.{method}";
                });

            engine.OnStepCompleted = (line, success, description) =>
                Dispatcher.BeginInvoke(() =>
                {
                    // Skip verbose lines for system.log/wait/set when successful
                    bool isVerbose = success && (
                        description.StartsWith("system.log(") ||
                        description.StartsWith("system.wait(") ||
                        description.StartsWith("set "));
                    if (!isVerbose)
                        AddOutput($"  [{line}] {description}");
                    RefreshWatch();
                });

            engine.OnLoopIteration = (line, current, total) =>
                Dispatcher.BeginInvoke(() =>
                    StatusText.Text = $"Loop L{line}: {current}/{(total > 0 ? total : "inf")}");

            engine.OnLog = message =>
                Dispatcher.BeginInvoke(() => AddOutput(message));

            engine.OnContextChanged = null; // watch refreshes on step complete, no need for per-change

            // Execute
            AddOutput("--- Recipe started ---");
            bool success = await engine.ExecuteAsync(program, _runContext, _cts.Token);
            AddOutput(success ? "--- Recipe completed ---" : "--- Recipe failed ---");
            StatusText.Text = success ? "Completed" : "Failed";
            RefreshWatch(force: true);
        }
        catch (OperationCanceledException)
        {
            AddOutput("--- Recipe stopped ---");
            StatusText.Text = "Stopped";
        }
        catch (Exception ex)
        {
            AddOutput($"[ERROR] {ex.Message}");
            StatusText.Text = "Error";
        }
        finally
        {
            engine.OnStepStarted = null;
            engine.OnStepCompleted = null;
            engine.OnLoopIteration = null;
            engine.OnLog = null;
            engine.OnContextChanged = null;
            engine.Release();
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        _context.RecipeEngine.ContinueStep();
    }

    public void StopExecution()
    {
        _cts?.Cancel();
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        RunBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        ContinueBtn.IsEnabled = running && StepModeCheck.IsChecked == true;
        Editor.IsReadOnly = running;
    }

    // --- Editor events ---

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        UpdateLineNumbers();

        // Debounce syntax highlighting
        _highlightTimer?.Stop();
        _highlightTimer?.Start();

        // Auto-trigger autocomplete
        if (!_isRunning)
        {
            var wordInfo = GetWordAtCaret();
            if (wordInfo != null)
            {
                var (prefix, afterDot, _) = wordInfo.Value;
                if (afterDot || prefix.Length >= 2)
                {
                    if (AutoCompletePopup.IsOpen)
                        UpdateAutoComplete();
                    else
                        TriggerAutoComplete();
                }
                else if (prefix.Length == 0 && !afterDot)
                {
                    AutoCompletePopup.IsOpen = false;
                }
            }
            else
            {
                AutoCompletePopup.IsOpen = false;
            }

            // Parameter hints
            UpdateParamHint();
        }
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextChanged) return;
        UpdateParamHint();
    }

    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Sync line number gutter scroll with editor scroll
        LineNumberScroll.ScrollToVerticalOffset(Editor.VerticalOffset);
    }

    private void UpdateLineNumbers()
    {
        var text = GetEditorText();
        int lineCount = text.Split('\n').Length;
        var sb = new StringBuilder();
        for (int i = 1; i <= lineCount; i++)
            sb.AppendLine(i.ToString());
        LineNumbers.Text = sb.ToString();
    }

    // --- Autocomplete ---

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AutoCompletePopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (AutoCompleteList.SelectedIndex < AutoCompleteList.Items.Count - 1)
                        AutoCompleteList.SelectedIndex++;
                    AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Up:
                    if (AutoCompleteList.SelectedIndex > 0)
                        AutoCompleteList.SelectedIndex--;
                    AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Return:
                    if (AutoCompleteList.SelectedItem is AutoCompleteItem selected)
                        CommitAutoComplete(selected);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    AutoCompletePopup.IsOpen = false;
                    ParamHintPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }
        else
        {
            // Ctrl+Space triggers autocomplete manually
            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                TriggerAutoComplete();
                e.Handled = true;
                return;
            }
        }
    }

    private void TriggerAutoComplete()
    {
        var wordInfo = GetWordAtCaret();
        if (wordInfo == null) return;

        var (prefix, afterDot, deviceName) = wordInfo.Value;
        _autoCompletePrefix = prefix;
        _autoCompleteAfterDot = afterDot;

        var items = BuildAutoCompleteItems(prefix, afterDot, deviceName);
        if (items.Count == 0)
        {
            AutoCompletePopup.IsOpen = false;
            return;
        }

        AutoCompleteList.ItemsSource = items;
        AutoCompleteList.SelectedIndex = 0;

        // Position popup near caret
        var caretRect = Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        AutoCompletePopup.HorizontalOffset = caretRect.Left + 4;
        AutoCompletePopup.VerticalOffset = caretRect.Bottom + 2;
        AutoCompletePopup.IsOpen = true;
    }

    private void UpdateAutoComplete()
    {
        if (!AutoCompletePopup.IsOpen) return;

        var wordInfo = GetWordAtCaret();
        if (wordInfo == null)
        {
            AutoCompletePopup.IsOpen = false;
            return;
        }

        var (prefix, afterDot, deviceName) = wordInfo.Value;
        _autoCompletePrefix = prefix;
        _autoCompleteAfterDot = afterDot;

        var items = BuildAutoCompleteItems(prefix, afterDot, deviceName);
        if (items.Count == 0)
        {
            AutoCompletePopup.IsOpen = false;
            return;
        }

        AutoCompleteList.ItemsSource = items;
        AutoCompleteList.SelectedIndex = 0;
    }

    private (string prefix, bool afterDot, string? deviceName)? GetWordAtCaret()
    {
        try
        {
            var caret = Editor.CaretPosition;
            var lineStart = caret.GetLineStartPosition(0);
            if (lineStart == null) return null;

            var textBefore = new TextRange(lineStart, caret).Text;
            if (string.IsNullOrEmpty(textBefore)) return ("", false, null);

            // Check if we're after a dot: "DeviceName.prefix"
            // Walk backward to find the current word
            int i = textBefore.Length - 1;
            while (i >= 0 && (char.IsLetterOrDigit(textBefore[i]) || textBefore[i] == '_' || textBefore[i] == '-'))
                i--;

            var currentWord = textBefore[(i + 1)..];

            // Check if char before the word is a dot
            if (i >= 0 && textBefore[i] == '.')
            {
                // Find device name before the dot
                int j = i - 1;
                while (j >= 0 && (char.IsLetterOrDigit(textBefore[j]) || textBefore[j] == '_' || textBefore[j] == '-'))
                    j--;
                var deviceName = textBefore[(j + 1)..i];
                if (!string.IsNullOrEmpty(deviceName))
                    return (currentWord, true, deviceName);
            }

            return (currentWord, false, null);
        }
        catch
        {
            return null;
        }
    }

    private List<AutoCompleteItem> BuildAutoCompleteItems(string prefix, bool afterDot, string? deviceName)
    {
        var items = new List<AutoCompleteItem>();
        var registry = _context.RecipeEngine.Methods;

        if (afterDot && deviceName != null)
        {
            // Show methods for this device
            var methods = registry.GetMethodNames(deviceName);
            foreach (var method in methods)
            {
                if (prefix.Length == 0 || method.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    items.Add(new AutoCompleteItem
                    {
                        Label = method,
                        Detail = "",
                        Icon = "M",
                        IconColor = ColorMethod.ToString(),
                        CompletionText = method
                    });
            }
        }
        else
        {
            // Show device names
            foreach (var dev in registry.GetDeviceNames())
            {
                if (prefix.Length == 0 || dev.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var devType = registry.GetDeviceType(dev) ?? "";
                    items.Add(new AutoCompleteItem
                    {
                        Label = dev,
                        Detail = devType,
                        Icon = GetDeviceIcon(devType),
                        IconColor = ColorDevice.ToString(),
                        CompletionText = dev
                    });
                }
            }

            // Show language keywords
            foreach (var kw in LanguageKeywords)
            {
                if (prefix.Length == 0 || kw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    items.Add(new AutoCompleteItem
                    {
                        Label = kw,
                        Detail = "keyword",
                        Icon = "K",
                        IconColor = ColorKeyword.ToString(),
                        CompletionText = kw
                    });
            }
        }

        return items;
    }

    private static string GetDeviceIcon(string deviceType)
    {
        return deviceType switch
        {
            "motion" => "M",
            "io" => "I",
            "pneumatic" => "P",
            "camera" => "C",
            "currentmeasurement" => "A",
            "lasercontroller" => "L",
            "system" => "S",
            _ => "D"
        };
    }

    private void CommitAutoComplete(AutoCompleteItem item)
    {
        AutoCompletePopup.IsOpen = false;

        try
        {
            // Get the full text, find caret position, do string surgery, put it back
            var text = GetEditorText();
            var caretCharPos = GetTextCharOffset(Editor.Document.ContentStart, Editor.CaretPosition);

            // Remove the prefix that was already typed
            int prefixLen = _autoCompletePrefix.Length;
            int insertPos = caretCharPos - prefixLen;
            if (insertPos < 0) insertPos = 0;

            text = text.Remove(insertPos, prefixLen);

            // Build completion text
            var insertText = item.CompletionText;
            if (!_autoCompleteAfterDot && item.Icon != "K")
                insertText += ".";
            if (_autoCompleteAfterDot)
                insertText += "(";

            text = text.Insert(insertPos, insertText);
            int newCaretPos = insertPos + insertText.Length;

            // Rewrite editor
            _suppressTextChanged = true;
            Editor.Document.Blocks.Clear();
            Editor.Document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0), LineHeight = 1 });
            _suppressTextChanged = false;

            // Restore caret
            var restored = GetPointerAtTextCharOffset(Editor.Document.ContentStart, newCaretPos);
            if (restored != null)
                Editor.CaretPosition = restored;

            Editor.Focus();
            UpdateLineNumbers();

            // Re-trigger highlighting
            _highlightTimer?.Stop();
            _highlightTimer?.Start();
        }
        catch { /* best effort */ }

        // If we just inserted a device name + dot, show method completions
        if (!_autoCompleteAfterDot && item.Icon != "K")
        {
            Dispatcher.BeginInvoke(() => TriggerAutoComplete(), DispatcherPriority.Input);
        }
    }

    private void AutoCompleteList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void AutoCompleteList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AutoCompleteList.SelectedItem is AutoCompleteItem selected)
            CommitAutoComplete(selected);
    }

    // --- Parameter hints ---

    private void UpdateParamHint()
    {
        try
        {
            var callInfo = GetMethodCallAtCaret();
            if (callInfo == null)
            {
                ParamHintPopup.IsOpen = false;
                return;
            }

            var (device, method, currentParamIndex) = callInfo.Value;
            var registry = _context.RecipeEngine.Methods;
            var paramInfo = registry.GetParamInfo(device, method);

            if (paramInfo == null || paramInfo.Params.Count == 0)
            {
                // Show method name with "()" for no-param methods
                ParamHintSignature.Inlines.Clear();
                ParamHintSignature.Inlines.Add(new Run($"{device}.{method}()") { Foreground = ColorPunctuation });
                ParamHintDetail.Text = "No parameters";
                PositionParamHint();
                return;
            }

            // Build signature with current param highlighted
            ParamHintSignature.Inlines.Clear();
            ParamHintSignature.Inlines.Add(new Run($"{device}.{method}(") { Foreground = ColorPunctuation });

            for (int i = 0; i < paramInfo.Params.Count; i++)
            {
                if (i > 0)
                    ParamHintSignature.Inlines.Add(new Run(", ") { Foreground = ColorPunctuation });

                var p = paramInfo.Params[i];
                var paramText = $"{p.Name}: {p.Type}";
                if (!p.Required) paramText = $"[{paramText}]";

                if (i == currentParamIndex)
                {
                    // Highlight current parameter
                    ParamHintSignature.Inlines.Add(new Run(paramText)
                    {
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold
                    });
                }
                else
                {
                    ParamHintSignature.Inlines.Add(new Run(paramText) { Foreground = ColorPunctuation });
                }
            }
            ParamHintSignature.Inlines.Add(new Run(")") { Foreground = ColorPunctuation });

            // Show description of current param
            if (currentParamIndex >= 0 && currentParamIndex < paramInfo.Params.Count)
            {
                var current = paramInfo.Params[currentParamIndex];
                var detail = current.Description ?? "";
                if (current.Default != null)
                    detail += $" (default: {current.Default})";
                ParamHintDetail.Text = detail;
                ParamHintDetail.Visibility = string.IsNullOrEmpty(detail)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                ParamHintDetail.Visibility = Visibility.Collapsed;
            }

            PositionParamHint();
        }
        catch
        {
            ParamHintPopup.IsOpen = false;
        }
    }

    private void PositionParamHint()
    {
        var caretRect = Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        ParamHintPopup.HorizontalOffset = Math.Max(caretRect.Left - 20, 0);
        ParamHintPopup.VerticalOffset = caretRect.Top - 40; // above the line
        if (ParamHintPopup.VerticalOffset < 0)
            ParamHintPopup.VerticalOffset = caretRect.Bottom + 2; // below if no room above
        ParamHintPopup.IsOpen = true;
    }

    /// <summary>
    /// Detect if caret is inside a method call's parentheses.
    /// Returns (deviceName, methodName, currentParamIndex) or null.
    /// </summary>
    private (string device, string method, int paramIndex)? GetMethodCallAtCaret()
    {
        var caret = Editor.CaretPosition;
        var lineStart = caret.GetLineStartPosition(0);
        if (lineStart == null) return null;

        var textBefore = new TextRange(lineStart, caret).Text;
        if (string.IsNullOrEmpty(textBefore)) return null;

        // Count unmatched open parens from end — are we inside ()?
        int depth = 0;
        int parenPos = -1;
        for (int i = textBefore.Length - 1; i >= 0; i--)
        {
            if (textBefore[i] == ')') depth++;
            else if (textBefore[i] == '(')
            {
                depth--;
                if (depth < 0) { parenPos = i; break; }
            }
        }
        if (parenPos < 0) return null;

        // Count commas between parenPos and caret to determine param index
        int commaCount = 0;
        for (int i = parenPos + 1; i < textBefore.Length; i++)
        {
            if (textBefore[i] == ',') commaCount++;
        }

        // Extract device.method before the paren
        var beforeParen = textBefore[..parenPos].TrimEnd();
        var dotPos = beforeParen.LastIndexOf('.');
        if (dotPos < 0) return null;

        var method = beforeParen[(dotPos + 1)..].Trim();
        var devicePart = beforeParen[..dotPos].TrimEnd();

        // Extract device name (last identifier before dot)
        int devStart = devicePart.Length - 1;
        while (devStart >= 0 && (char.IsLetterOrDigit(devicePart[devStart])
            || devicePart[devStart] == '_' || devicePart[devStart] == '-'))
            devStart--;
        var device = devicePart[(devStart + 1)..];

        if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(method)) return null;

        return (device, method, commaCount);
    }

    // --- Watch ---

    private void RefreshWatch(bool force = false)
    {
        if (_runContext == null) return;
        // Throttle to max every 200ms during execution to avoid UI freeze
        var now = DateTime.UtcNow;
        if (!force && (now - _lastWatchRefresh).TotalMilliseconds < 200) return;
        _lastWatchRefresh = now;

        var snapshot = _runContext.GetSnapshot();
        // Skip .unit metadata entries
        _watchItems.Clear();
        foreach (var (key, value, _) in snapshot)
        {
            if (key.EndsWith(".unit")) continue;
            _watchItems.Add(new WatchItem { Variable = key, Value = value });
        }
    }

    // --- Copy to clipboard ---

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        CopyListBoxToClipboard(OutputList);
    }

    private void CopyErrors_Click(object sender, RoutedEventArgs e)
    {
        CopyListBoxToClipboard(ErrorList);
    }

    private void CopyListBoxToClipboard(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            StatusText.Text = "Nothing to copy";
            return;
        }

        var sb = new StringBuilder();
        foreach (var item in list.Items)
            sb.AppendLine(item.ToString());
        var text = sb.ToString();
        var count = list.Items.Count;

        // Fire-and-forget on a separate STA thread — never blocks UI
        var thread = new Thread(() =>
        {
            try { Clipboard.SetText(text); }
            catch { /* ignore clipboard lock */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        StatusText.Text = $"Copied {count} line(s) to clipboard";
    }

    // --- Output ---

    private void AddOutput(string message)
    {
        OutputList.Items.Add(message);
        if (OutputList.Items.Count > 0)
            OutputList.ScrollIntoView(OutputList.Items[^1]);
    }

    // --- Data types ---

    public class WatchItem
    {
        public string Variable { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class AutoCompleteItem
    {
        public string Label { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Icon { get; set; } = "";
        public string IconColor { get; set; } = "#D4D4D4";
        public string CompletionText { get; set; } = "";
    }
}
