using System.Text.Json;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Tests;

public sealed class ActionValidatorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ActionValidation Validate(string? op, string targetJson, string? argsJson = null) =>
        ActionValidator.Validate(op, Json(targetJson), argsJson is null ? null : Json(argsJson));

    private const string ElementTarget = """{"kind":"element","index":3,"state_id":"st-8f21cd04"}""";
    private const string CoordinateTarget = """{"kind":"coordinate","x":100,"y":50}""";

    [Fact]
    public void ValidElementClick()
    {
        var result = Validate("click", ElementTarget, """{"button":"left","count":1}""");
        Assert.True(result.Ok);
        Assert.Equal("click", result.Plan!.Op);
        Assert.Equal(TargetKind.Element, result.Plan.Kind);
        Assert.Equal(3, result.Plan.ElementIndex);
        Assert.Equal("st-8f21cd04", result.Plan.StateId);
    }

    [Fact]
    public void ValidCoordinateScrollDefaultsToOnePage()
    {
        var result = Validate("scroll", CoordinateTarget, """{"direction":"down"}""");
        Assert.True(result.Ok);
        Assert.Equal(TargetKind.Coordinate, result.Plan!.Kind);
        Assert.Equal(100, result.Plan.X);
        Assert.Equal(1.0, result.Plan.ScrollPages);
    }

    [Fact]
    public void MissingTargetIsInvalidArgument()
    {
        var result = Validate("click", "null");
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
        Assert.Equal(DispatchStatus.NotSent, result.Error.DispatchStatus);
        Assert.False(result.Error.ActionSent);
    }

    [Theory]
    [InlineData("right_click")]
    [InlineData("double_click")]
    [InlineData("drag")]
    [InlineData("paste")]
    public void UnknownOpsAreUnsupportedAction(string op)
    {
        var result = Validate(op, ElementTarget);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.UnsupportedAction, result.Error!.Code);
        Assert.Equal(RetryHint.Never, result.Error.Retry);
    }

    [Theory]
    [InlineData("""{"kind":"element","index":3,"state_id":"st-XYZ"}""")]
    [InlineData("""{"kind":"element","index":-1,"state_id":"st-8f21cd04"}""")]
    [InlineData("""{"kind":"element","index":3}""")]
    [InlineData("""{"kind":"coordinate","x":-1,"y":2}""")]
    [InlineData("""{"kind":"coordinate","x":1}""")]
    [InlineData("""{"kind":"window","x":1,"y":2}""")]
    [InlineData("""{"kind":"element","index":3,"state_id":"st-8f21cd04","frame_id":"fr-1"}""")]
    public void MalformedTargetsAreInvalid(string targetJson)
    {
        var result = Validate("click", targetJson, """{"button":"left","count":1}""");
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void TypeRequiresElementTarget()
    {
        var result = Validate("type", CoordinateTarget, """{"text":"hi"}""");
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void KeyCombinationIsUnsupported()
    {
        var result = Validate("key", ElementTarget, """{"keys":"ctrl+s"}""");
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.UnsupportedAction, result.Error!.Code);
    }

    [Fact]
    public void UnknownSingleKeyIsInvalid()
    {
        var result = Validate("key", ElementTarget, """{"keys":"f1"}""");
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void RightClickOrDoubleClickIsUnsupported()
    {
        var right = Validate("click", ElementTarget, """{"button":"right","count":1}""");
        Assert.Equal(ErrorCodes.UnsupportedAction, right.Error!.Code);
        var twice = Validate("click", ElementTarget, """{"button":"left","count":2}""");
        Assert.Equal(ErrorCodes.UnsupportedAction, twice.Error!.Code);
    }

    [Fact]
    public void SetValueLengthIsCapped()
    {
        var tooLong = new string('x', 4001);
        var result = Validate("set_value", ElementTarget, "{\"value\":\"" + tooLong + "\"}");
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void TypeLengthIsCapped()
    {
        var tooLong = new string('x', 1501);
        var result = Validate("type", ElementTarget, "{\"text\":\"" + tooLong + "\"}");
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("20.5")]
    public void ScrollPagesOutOfRangeIsInvalid(string pages)
    {
        var result = Validate("scroll", ElementTarget, "{\"direction\":\"up\",\"pages\":" + pages + "}");
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void UnknownArgsKeysAreRejected()
    {
        var result = Validate("click", ElementTarget, """{"button":"left","count":1,"strategy":"uia"}""");
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void UnknownTargetKeysAreRejected()
    {
        var result = Validate("click", """{"kind":"coordinate","x":1,"y":2,"frame_id":"fr-x"}""", """{"button":"left","count":1}""");
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void ValidHotkeyIsCanonicalized()
    {
        var result = Validate("hotkey", ElementTarget, """{"keys":"Shift+Ctrl+S"}""");
        Assert.True(result.Ok);
        Assert.Equal("hotkey", result.Plan!.Op);
        Assert.Equal("ctrl+shift+s", result.Plan.Hotkey);
    }

    [Fact]
    public void HotkeyRequiresElementTarget()
    {
        var result = Validate("hotkey", CoordinateTarget, """{"keys":"ctrl+s"}""");
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Theory]
    [InlineData("""{"keys":"ctrl+"}""")]
    [InlineData("""{"keys":"ctrl+ctrl+s"}""")]
    [InlineData("""{"keys":"ctrl+unknown"}""")]
    [InlineData("""{}""")]
    public void MalformedHotkeyIsInvalidArgument(string argsJson)
    {
        var result = Validate("hotkey", ElementTarget, argsJson);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public void HotkeyAllowsSingleKey()
    {
        var result = Validate("hotkey", ElementTarget, """{"keys":"f5"}""");
        Assert.True(result.Ok);
        Assert.Equal("f5", result.Plan!.Hotkey);
    }
}
