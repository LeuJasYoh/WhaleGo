using System.IO;
using System.Reflection;
using System.Xml.Linq;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui;
using WhaleGo.Gui.Tray;

namespace WhaleGo.Gui.Tests;

internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public List<(string Id, string Gesture)> Registered { get; } = [];

    public List<string> Unregistered { get; } = [];

    public bool Result { get; set; } = true;

    public bool TryRegister(string id, string gesture, Action onPressed)
    {
        Registered.Add((id, gesture));
        return Result;
    }

    public void Unregister(string id) => Unregistered.Add(id);
}

public sealed class GuiShellContractTests
{
    /// <summary>Guards the public GuiShell surface T4 codes against (docs/GUI.md 4.6 / scaffold agreement).</summary>
    [Fact]
    public void PublicSurfaceIsStable()
    {
        var type = typeof(GuiShell);
        Assert.True(type.IsClass && type.IsSealed && typeof(IDisposable).IsAssignableFrom(type));

        var constructor = type.GetConstructor([typeof(IExecutorControl), typeof(IWindowTheme), typeof(IHotkeyRegistrar), typeof(GuiOptions)]);
        Assert.NotNull(constructor);

        Assert.Equal(typeof(IExecutorControl), type.GetProperty(nameof(GuiShell.Control))!.PropertyType);
        Assert.Equal(typeof(IWindowTheme), type.GetProperty(nameof(GuiShell.WindowTheme))!.PropertyType);
        Assert.Equal(typeof(IHotkeyRegistrar), type.GetProperty(nameof(GuiShell.Hotkeys))!.PropertyType);
        Assert.Equal(typeof(GuiOptions), type.GetProperty(nameof(GuiShell.Options))!.PropertyType);

        var exitEvent = type.GetEvent(nameof(GuiShell.ExitRequested));
        Assert.NotNull(exitEvent);
        Assert.Equal(typeof(EventHandler), exitEvent!.EventHandlerType);

        AssertMethod(type, nameof(GuiShell.ShowSettings));
        AssertMethod(type, nameof(GuiShell.HideToTray));
        AssertMethod(type, nameof(GuiShell.RefreshStatus));
        AssertMethod(type, nameof(GuiShell.RequestExit));
        AssertMethod(type, nameof(GuiShell.Dispose));
        var balloon = type.GetMethod(nameof(GuiShell.ShowBalloon), [typeof(string), typeof(string), typeof(bool)]);
        Assert.NotNull(balloon);
        Assert.Equal(typeof(void), balloon!.ReturnType);

        var optionNames = typeof(GuiOptions).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "ConfigPath", "ExecutablePath", "Headless", "LogsDirectory", "NoHotkey", "ProtocolVersion", "Version" },
            optionNames);
        Assert.Equal(typeof(bool), typeof(GuiOptions).GetProperty("Headless")!.PropertyType);
        Assert.Equal(typeof(string), typeof(GuiOptions).GetProperty("LogsDirectory")!.PropertyType);
    }

    private static void AssertMethod(Type type, string name)
    {
        var method = type.GetMethod(name, Type.EmptyTypes);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    [Fact]
    public void HeadlessShellIsInertAndRaisesExitRequested()
    {
        var control = new FakeExecutorControl();
        var windowTheme = new FakeWindowTheme();
        var hotkeys = new FakeHotkeyRegistrar();
        using var shell = new GuiShell(control, windowTheme, hotkeys, TestData.Options with { Headless = true });

        Assert.Same(control, shell.Control);
        Assert.Same(windowTheme, shell.WindowTheme);
        Assert.Same(hotkeys, shell.Hotkeys);
        Assert.True(shell.Options.Headless);

        using var exit = new ManualResetEventSlim(false);
        shell.ExitRequested += (_, _) => exit.Set();

        shell.ShowSettings();
        shell.HideToTray();
        shell.ShowBalloon("t", "m", important: true);
        shell.RefreshStatus();
        shell.RequestExit();

        Assert.True(exit.Wait(TimeSpan.FromSeconds(5)));
        shell.Dispose();
        shell.Dispose();
    }

    [Fact]
    public void GuiAssemblyReferencesOnlyContractsAndFramework()
    {
        var allowedPrefixes = new[] { "System.", "Microsoft.Win32.", "Microsoft.CSharp" };
        var allowedExact = new HashSet<string>(StringComparer.Ordinal)
        {
            "WhaleGo.Contracts", "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml",
            "Accessibility", "mscorlib", "netstandard",
        };
        var forbidden = new[] { "WhaleGo.Platform", "WhaleGo.Executor", "Microsoft.AspNetCore", "MahApps", "MaterialDesign", "HandyControl", "Hardcodet", "CommunityToolkit" };

        foreach (var reference in typeof(GuiShell).Assembly.GetReferencedAssemblies())
        {
            var name = reference.Name ?? string.Empty;
            Assert.DoesNotContain(forbidden, fragment => name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase));
            var allowed = allowedExact.Contains(name) || allowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(allowed, "unexpected reference in WhaleGo.Gui: " + name);
        }
    }

    [Fact]
    public void GuiProjectHasNoThirdPartyPackagesAndOnlyContractsProjectReference()
    {
        var document = XDocument.Load(Path.Combine(RepoPaths.GuiProjectDirectory, "WhaleGo.Gui.csproj"));
        var packageReferences = document.Descendants().Where(element => element.Name.LocalName == "PackageReference").ToList();
        Assert.Empty(packageReferences);

        var projectReferences = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .ToList();
        Assert.Single(projectReferences);
        Assert.Contains("WhaleGo.Contracts", projectReferences[0]);
    }

    [Fact]
    public void TrayIconAssetsAreEmbeddedMultiSizeIcons()
    {
        var resources = TrayIconProvider.ListEmbeddedIconResources();
        Assert.Equal(10, resources.Count);
        foreach (var expected in TrayIcons.AllResourceNames())
        {
            Assert.Contains(expected, resources);
        }

        var assembly = typeof(TrayIconProvider).Assembly;
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            Assert.NotNull(stream);
            var sizes = ReadIconSizes(stream!);
            Assert.Contains(16, sizes);
            Assert.Contains(20, sizes);
            Assert.Contains(24, sizes);
            Assert.Contains(32, sizes);
            Assert.Contains(48, sizes);
            Assert.Contains(256, sizes);

            stream.Position = 0;
            using var icon = new System.Drawing.Icon(stream);
            Assert.True(icon.Width >= 16);
        }
    }

    private static List<int> ReadIconSizes(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadUInt32();
            reader.ReadUInt32();
            sizes.Add(width == 0 ? 256 : width);
        }

        return sizes;
    }
}

/// <summary>Repository paths resolved from the test output directory (no hard-coded absolute paths).</summary>
internal static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string GuiProjectDirectory { get; } = Path.Combine(RepoRoot, "windows", "src", "WhaleGo.Gui");

    public static string ViewsDirectory { get; } = Path.Combine(GuiProjectDirectory, "Views");

    public static string ThemesDirectory { get; } = Path.Combine(GuiProjectDirectory, "Themes");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "windows", "src", "WhaleGo.Gui")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
    }
}