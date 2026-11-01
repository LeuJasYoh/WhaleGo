using System.Text.Json;
using System.Text.RegularExpressions;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Domain;

public sealed record ActionValidation(ActionPlan? Plan, GoError? Error)
{
    public bool Ok => Plan is not null;

    public static ActionValidation Success(ActionPlan plan) => new(plan, null);

    public static ActionValidation Failure(GoError error) => new(null, error);
}

/// <summary>Pure action validator: the v1 narrow action surface (docs/PROTOCOL.md section 3.7) and target rules.</summary>
public static partial class ActionValidator
{
    private static readonly HashSet<string> KnownOps = new(StringComparer.Ordinal)
    {
        "click", "set_value", "type", "key", "hotkey", "scroll",
    };

    private static readonly HashSet<string> KeyNames = new(StringComparer.Ordinal)
    {
        "enter", "tab", "escape", "backspace", "delete", "up", "down", "left", "right",
        "pageup", "pagedown", "home", "end", "space",
    };

    private static readonly string[] ElementTargetKeys = ["kind", "index", "state_id"];
    private static readonly string[] CoordinateTargetKeys = ["kind", "x", "y"];
    private static readonly string[] ClickArgKeys = ["button", "count", "modifiers"];
    private static readonly string[] SetValueArgKeys = ["value"];
    private static readonly string[] TypeArgKeys = ["text"];
    private static readonly string[] KeyArgKeys = ["keys"];
    private static readonly string[] HotkeyArgKeys = ["keys"];
    private static readonly string[] ScrollArgKeys = ["direction", "pages"];

    public const string StateIdPattern = "^st-[0-9a-f]{8}$";

    [GeneratedRegex(StateIdPattern)]
    private static partial Regex StateIdRegex();

    public static ActionValidation Validate(string? op, JsonElement target, JsonElement? args)
    {
        if (string.IsNullOrWhiteSpace(op))
        {
            return Fail(ErrorCodes.InvalidArgument, "缺少 op。");
        }

        if (!KnownOps.Contains(op))
        {
            return Fail(ErrorCodes.UnsupportedAction, $"v1 不支持动作 {op}。");
        }

        if (target.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Fail(ErrorCodes.InvalidArgument, "缺少 target：所有动作必须带 target。");
        }

        if (target.ValueKind != JsonValueKind.Object)
        {
            return Fail(ErrorCodes.InvalidArgument, "target 必须是对象。");
        }

        var kindText = ReadString(target, "kind");
        if (kindText is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "target.kind 缺失。");
        }

        var isElement = string.Equals(kindText, "element", StringComparison.Ordinal);
        var isCoordinate = string.Equals(kindText, "coordinate", StringComparison.Ordinal);
        if (!isElement && !isCoordinate)
        {
            return Fail(ErrorCodes.InvalidArgument, "target.kind 只能是 element 或 coordinate。");
        }

        var invalidTargetKey = FirstUnknownKey(target, isElement ? ElementTargetKeys : CoordinateTargetKeys);
        if (invalidTargetKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"target 包含不支持的字段：{invalidTargetKey}。");
        }

        int? elementIndex = null;
        string? stateId = null;
        int? x = null;
        int? y = null;
        if (isElement)
        {
            var rawIndex = ReadInt(target, "index");
            if (rawIndex is null || rawIndex < 0)
            {
                return Fail(ErrorCodes.InvalidArgument, "target.index 缺失或非法。");
            }

            stateId = ReadString(target, "state_id");
            if (stateId is null || !StateIdRegex().IsMatch(stateId))
            {
                return Fail(ErrorCodes.InvalidArgument, "target.state_id 缺失或格式非法（st-xxxxxxxx）。");
            }

            elementIndex = rawIndex;
        }
        else
        {
            x = ReadInt(target, "x");
            y = ReadInt(target, "y");
            if (x is null || y is null || x < 0 || y < 0)
            {
                return Fail(ErrorCodes.InvalidArgument, "target.x/target.y 缺失或非法。");
            }
        }

        var argsObject = args is null || args.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? (JsonElement?)null
            : args.Value;
        if (argsObject is not null && argsObject.Value.ValueKind != JsonValueKind.Object)
        {
            return Fail(ErrorCodes.InvalidArgument, "args 必须是对象。");
        }

        var targetKind = isElement ? TargetKind.Element : TargetKind.Coordinate;
        var plan = op switch
        {
            "click" => ValidateClick(argsObject, targetKind, elementIndex, stateId, x, y),
            "set_value" => ValidateSetValue(argsObject, targetKind, elementIndex, stateId),
            "type" => ValidateType(argsObject, targetKind, elementIndex, stateId),
            "key" => ValidateKey(argsObject, targetKind, elementIndex, stateId),
            "hotkey" => ValidateHotkey(argsObject, targetKind, elementIndex, stateId),
            "scroll" => ValidateScroll(argsObject, targetKind, elementIndex, stateId, x, y),
            _ => Fail(ErrorCodes.UnsupportedAction, $"v1 不支持动作 {op}。"),
        };
        return plan;
    }

    private static ActionValidation ValidateClick(JsonElement? args, TargetKind kind, int? index, string? stateId, int? x, int? y)
    {
        if (args is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "click 缺少 args（button/count）。");
        }

        var invalidKey = FirstUnknownKey(args.Value, ClickArgKeys);
        if (invalidKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"args 包含不支持的字段：{invalidKey}。");
        }

        var button = ReadString(args.Value, "button");
        var count = ReadInt(args.Value, "count");
        if (button is null || count is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "click 需要 args.button 与 args.count。");
        }

        var modifiers = ReadString(args.Value, "modifiers") ?? string.Empty;
        if (!string.Equals(button, "left", StringComparison.Ordinal) || count != 1 || modifiers.Length > 0)
        {
            return Fail(ErrorCodes.UnsupportedAction, "v1 仅支持左键单击（button=left,count=1,modifiers=\"\"）。");
        }

        return ActionValidation.Success(new ActionPlan("click", kind, index, stateId, x, y, "left", 1, string.Empty));
    }

    private static ActionValidation ValidateSetValue(JsonElement? args, TargetKind kind, int? index, string? stateId)
    {
        if (kind != TargetKind.Element)
        {
            return Fail(ErrorCodes.InvalidArgument, "set_value 只接受 element target。");
        }

        if (args is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "set_value 缺少 args.value。");
        }

        var invalidKey = FirstUnknownKey(args.Value, SetValueArgKeys);
        if (invalidKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"args 包含不支持的字段：{invalidKey}。");
        }

        var value = ReadString(args.Value, "value");
        if (value is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "set_value 需要字符串 args.value。");
        }

        if (value.Length > 4000)
        {
            return Fail(ErrorCodes.InvalidArgument, "args.value 超过 4000 字符上限。");
        }

        return ActionValidation.Success(new ActionPlan("set_value", kind, index, stateId, SetValue: value));
    }

    private static ActionValidation ValidateType(JsonElement? args, TargetKind kind, int? index, string? stateId)
    {
        if (kind != TargetKind.Element)
        {
            return Fail(ErrorCodes.InvalidArgument, "type 只接受 element target（v1 不向当前焦点窗口盲发）。");
        }

        if (args is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "type 缺少 args.text。");
        }

        var invalidKey = FirstUnknownKey(args.Value, TypeArgKeys);
        if (invalidKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"args 包含不支持的字段：{invalidKey}。");
        }

        var text = ReadString(args.Value, "text");
        if (text is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "type 需要字符串 args.text。");
        }

        if (text.Length > 1500)
        {
            return Fail(ErrorCodes.InvalidArgument, "args.text 超过 1500 字符上限。");
        }

        return ActionValidation.Success(new ActionPlan("type", kind, index, stateId, TypeText: text));
    }

    private static ActionValidation ValidateKey(JsonElement? args, TargetKind kind, int? index, string? stateId)
    {
        if (kind != TargetKind.Element)
        {
            return Fail(ErrorCodes.InvalidArgument, "key 只接受 element target。");
        }

        if (args is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "key 缺少 args.keys。");
        }

        var invalidKey = FirstUnknownKey(args.Value, KeyArgKeys);
        if (invalidKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"args 包含不支持的字段：{invalidKey}。");
        }

        var keys = ReadString(args.Value, "keys");
        if (keys is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "key 需要字符串 args.keys。");
        }

        if (keys.Contains('+') || keys.Contains(' '))
        {
            return Fail(ErrorCodes.UnsupportedAction, "v1 不支持组合键/修饰键，只支持单键。");
        }

        if (!KeyNames.Contains(keys))
        {
            return Fail(ErrorCodes.InvalidArgument, $"不支持的按键：{keys}。");
        }

        return ActionValidation.Success(new ActionPlan("key", kind, index, stateId, Key: keys));
    }

    private static ActionValidation ValidateHotkey(JsonElement? args, TargetKind kind, int? index, string? stateId)
    {
        if (kind != TargetKind.Element)
        {
            return Fail(ErrorCodes.InvalidArgument, "hotkey 只接受 element target（先 observe 拿到 state_id+index）。");
        }

        if (args is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "hotkey 缺少 args.keys（例如 ctrl+s）。");
        }

        var invalidKey = FirstUnknownKey(args.Value, HotkeyArgKeys);
        if (invalidKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"args 包含不支持的字段：{invalidKey}。");
        }

        var keys = ReadString(args.Value, "keys");
        if (keys is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "hotkey 需要字符串 args.keys（例如 ctrl+shift+s）。");
        }

        if (!HotkeySpec.TryParse(keys, out var spec))
        {
            return Fail(ErrorCodes.InvalidArgument, "组合键格式非法：示例 ctrl+s、ctrl+shift+esc、f5。");
        }

        return ActionValidation.Success(new ActionPlan("hotkey", kind, index, stateId, Hotkey: spec.Canonical));
    }

    private static ActionValidation ValidateScroll(JsonElement? args, TargetKind kind, int? index, string? stateId, int? x, int? y)
    {
        if (args is null)
        {
            return Fail(ErrorCodes.InvalidArgument, "scroll 缺少 args.direction。");
        }

        var invalidKey = FirstUnknownKey(args.Value, ScrollArgKeys);
        if (invalidKey is not null)
        {
            return Fail(ErrorCodes.InvalidArgument, $"args 包含不支持的字段：{invalidKey}。");
        }

        var direction = ReadString(args.Value, "direction");
        if (direction is null || (direction != "up" && direction != "down"))
        {
            return Fail(ErrorCodes.InvalidArgument, "scroll 需要 args.direction = up|down。");
        }

        var pages = 1.0;
        if (args.Value.TryGetProperty("pages", out var pagesElement))
        {
            if (pagesElement.ValueKind != JsonValueKind.Number || !pagesElement.TryGetDouble(out pages))
            {
                return Fail(ErrorCodes.InvalidArgument, "args.pages 必须是数字。");
            }
        }

        if (pages is < 0.25 or > 20)
        {
            return Fail(ErrorCodes.InvalidArgument, "args.pages 必须在 0.25..20 之间。");
        }

        return ActionValidation.Success(new ActionPlan("scroll", kind, index, stateId, x, y, ScrollDirection: direction, ScrollPages: pages));
    }

    private static ActionValidation Fail(string code, string? message = null) =>
        ActionValidation.Failure(GoErrorFactory.FromCode(code, message: message));

    private static string? FirstUnknownKey(JsonElement obj, string[] allowed)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                return property.Name;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
