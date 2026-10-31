using System.Diagnostics;
using System.Drawing;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Privacy;

namespace WhaleGo.Platform.Uia;

/// <summary>One raw UIA node before the Contracts.Domain budget shaper decides what reaches the model.</summary>
public sealed record UiaRawNode(
    int Index,
    int Depth,
    AutomationElement Element,
    string Kind,
    string? Title,
    string? Value,
    bool ValueTruncated,
    string? AutomationId,
    IReadOnlyList<string> Actions,
    bool Enabled,
    bool Selected,
    RectI BoundsScreenDiag,
    bool IsPassword,
    string? ClassName);

public sealed record UiaTreeBuildResult(
    IReadOnlyList<UiaRawNode> Nodes,
    bool UiaUnavailable,
    bool BudgetExceeded,
    bool TimedOut,
    long ElapsedMs)
{
    public static UiaTreeBuildResult Unavailable(long elapsedMs) => new(Array.Empty<UiaRawNode>(), true, false, false, elapsedMs);
}

/// <summary>
/// Walks the UIA tree from a top-level window root with hard limits (2000 nodes / 2 s budget, docs/DESIGN §5.4):
/// sequential stable index + depth, normalized control kinds, Name as title, ValuePattern/TextPattern value truncated
/// to max_value_chars, pattern-derived action names, and IsPassword values masked as "***" (the real value is never read).
/// Every UIA read is wrapped so a misbehaving provider only loses that property, never the whole observation.
/// </summary>
public sealed class UiaTreeBuilder
{
    public const int MaxNodesHardCap = 2000;
    public const int DefaultMaxValueChars = 48;
    public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromMilliseconds(2000);

    private readonly UiaEngine _engine;

    public UiaTreeBuilder(UiaEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public UiaTreeBuildResult Build(
        IntPtr hwnd,
        int maxValueChars = DefaultMaxValueChars,
        bool maskPasswordFields = true,
        int maxNodes = MaxNodesHardCap,
        TimeSpan? timeBudget = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var valueCap = Math.Clamp(maxValueChars, 1, 4000);
        var nodeCap = Math.Clamp(maxNodes, 1, MaxNodesHardCap);
        var budget = timeBudget ?? DefaultTimeBudget;

        var root = _engine.TryGetRoot(hwnd);
        if (root is null)
        {
            return UiaTreeBuildResult.Unavailable(stopwatch.ElapsedMilliseconds);
        }

        var nodes = new List<UiaRawNode>(Math.Min(nodeCap, 256));
        var budgetExceeded = false;
        var timedOut = false;
        var index = 0;

        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            if (nodes.Count >= nodeCap)
            {
                budgetExceeded = true;
                break;
            }

            if (stopwatch.Elapsed > budget)
            {
                budgetExceeded = true;
                timedOut = true;
                break;
            }

            var (element, depth) = stack.Pop();
            UiaRawNode? node;
            try
            {
                node = Describe(element, index, depth, valueCap, maskPasswordFields);
            }
            catch
            {
                node = null;
            }

            if (node is null)
            {
                continue;
            }

            nodes.Add(node);
            index++;

            if (depth >= 64)
            {
                continue;
            }

            var children = _engine.SafeRead(() => element.FindAllChildren());
            if (children is null || children.Length == 0)
            {
                continue;
            }

            for (var i = children.Length - 1; i >= 0; i--)
            {
                stack.Push((children[i], depth + 1));
            }
        }

        stopwatch.Stop();
        return new UiaTreeBuildResult(nodes, false, budgetExceeded, timedOut, stopwatch.ElapsedMilliseconds);
    }

    private UiaRawNode? Describe(AutomationElement element, int index, int depth, int maxValueChars, bool maskPasswordFields)
    {
        var properties = element.Properties;
        var controlType = Read(() => properties.ControlType.ValueOrDefault, ControlType.Unknown);
        var kind = depth == 0 ? "window" : NormalizeKind(controlType);
        var title = Trim(Read(() => properties.Name.ValueOrDefault, (string?)null));
        var automationId = Read(() => properties.AutomationId.ValueOrDefault, (string?)null);
        var className = Read(() => properties.ClassName.ValueOrDefault, (string?)null);
        var enabled = Read(() => properties.IsEnabled.ValueOrDefault, true);
        var isPassword = Read(() => properties.IsPassword.ValueOrDefault, false);
        var bounds = Read(() => properties.BoundingRectangle.ValueOrDefault, default(Rectangle));

        var (value, valueTruncated) = ReadValue(element, kind, maxValueChars, isPassword, maskPasswordFields);
        var actions = DetectActions(element);
        var selected = ReadSelected(element);

        return new UiaRawNode(
            index,
            depth,
            element,
            kind,
            string.IsNullOrEmpty(title) ? null : title,
            value,
            valueTruncated,
            string.IsNullOrEmpty(automationId) ? null : automationId,
            actions,
            enabled,
            selected,
            new RectI(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            isPassword,
            string.IsNullOrEmpty(className) ? null : className);
    }

    private (string? Value, bool Truncated) ReadValue(
        AutomationElement element,
        string kind,
        int maxValueChars,
        bool isPassword,
        bool maskPasswordFields)
    {
        // Privacy hard rule (PROTOCOL §11.1): password fields are never read; the wire value is always the mask,
        // even when privacy.maskPasswordFields is off (the flag cannot un-mask a password control in v1).
        _ = maskPasswordFields;
        if (isPassword)
        {
            return (Contracts.Domain.PrivacyRules.MaskedValue, false);
        }

        string? raw = null;
        if (Read(() => element.Patterns.Value.IsSupported, false))
        {
            raw = Read(() => element.Patterns.Value.PatternOrDefault!.Value.ValueOrDefault, (string?)null);
        }

        if (string.IsNullOrEmpty(raw) && (kind is "edit" or "document"))
        {
            if (Read(() => element.Patterns.Text.IsSupported, false))
            {
                raw = Read(
                    () => element.Patterns.Text.PatternOrDefault!.DocumentRange.GetText(maxValueChars + 1),
                    (string?)null);
            }
        }

        if (raw is null)
        {
            return (null, false);
        }

        return TruncateRunes(raw, maxValueChars);
    }

    private static IReadOnlyList<string> DetectActions(AutomationElement element)
    {
        var actions = new List<string>(6);
        var patterns = Read(() => element.Patterns, null);
        if (patterns is null)
        {
            return actions;
        }

        if (Read(() => patterns.Invoke.IsSupported, false))
        {
            actions.Add("invoke");
        }

        if (Read(() => patterns.Toggle.IsSupported, false))
        {
            actions.Add("toggle");
        }

        if (Read(() => patterns.SelectionItem.IsSupported, false))
        {
            actions.Add("select");
        }

        if (Read(() => patterns.ExpandCollapse.IsSupported, false))
        {
            actions.Add("expand_collapse");
        }

        if (Read(() => patterns.Value.IsSupported, false))
        {
            var readOnly = Read(() => patterns.Value.PatternOrDefault!.IsReadOnly.ValueOrDefault, true);
            if (!readOnly)
            {
                actions.Add("set_value");
            }
        }

        if (Read(() => patterns.Scroll.IsSupported, false))
        {
            actions.Add("scroll");
        }

        return actions;
    }

    private static bool ReadSelected(AutomationElement element)
    {
        var patterns = Read(() => element.Patterns, null);
        if (patterns is null)
        {
            return false;
        }

        if (Read(() => patterns.SelectionItem.IsSupported, false))
        {
            return Read(() => patterns.SelectionItem.PatternOrDefault!.IsSelected.ValueOrDefault, false);
        }

        if (Read(() => patterns.Toggle.IsSupported, false))
        {
            return Read(() => patterns.Toggle.PatternOrDefault!.ToggleState.ValueOrDefault, ToggleState.Off) == ToggleState.On;
        }

        return false;
    }

    /// <summary>ControlType normalization to the small closed kind vocabulary used by the wire tree.</summary>
    public static string NormalizeKind(ControlType controlType) => controlType switch
    {
        ControlType.Button => "button",
        ControlType.Calendar => "calendar",
        ControlType.CheckBox => "checkbox",
        ControlType.ComboBox => "combobox",
        ControlType.Custom => "custom",
        ControlType.DataGrid => "datagrid",
        ControlType.DataItem => "dataitem",
        ControlType.Document => "document",
        ControlType.Edit => "edit",
        ControlType.Group => "group",
        ControlType.Header => "header",
        ControlType.HeaderItem => "headeritem",
        ControlType.Hyperlink => "hyperlink",
        ControlType.Image => "image",
        ControlType.List => "list",
        ControlType.ListItem => "listitem",
        ControlType.MenuBar => "menubar",
        ControlType.Menu => "menu",
        ControlType.MenuItem => "menuitem",
        ControlType.Pane => "pane",
        ControlType.ProgressBar => "progressbar",
        ControlType.RadioButton => "radiobutton",
        ControlType.ScrollBar => "scrollbar",
        ControlType.SemanticZoom => "semanticzoom",
        ControlType.Separator => "separator",
        ControlType.Slider => "slider",
        ControlType.Spinner => "spinner",
        ControlType.SplitButton => "splitbutton",
        ControlType.StatusBar => "statusbar",
        ControlType.Tab => "tab",
        ControlType.TabItem => "tabitem",
        ControlType.Table => "table",
        ControlType.Text => "text",
        ControlType.Thumb => "thumb",
        ControlType.TitleBar => "titlebar",
        ControlType.ToolBar => "toolbar",
        ControlType.ToolTip => "tooltip",
        ControlType.Tree => "tree",
        ControlType.TreeItem => "treeitem",
        ControlType.Window => "window",
        _ => "custom",
    };

    /// <summary>Rune-safe truncation (never splits a surrogate pair); no ellipsis for wire values.</summary>
    public static (string Value, bool Truncated) TruncateRunes(string value, int maxRunes)
    {
        if (maxRunes <= 0)
        {
            return (string.Empty, value.Length > 0);
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxRunes + 8));
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count >= maxRunes)
            {
                return (builder.ToString(), true);
            }

            builder.Append(rune.ToString());
            count++;
        }

        return (builder.ToString(), false);
    }

    private static string? Trim(string? value) => value ?? string.Empty;

    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }
}
