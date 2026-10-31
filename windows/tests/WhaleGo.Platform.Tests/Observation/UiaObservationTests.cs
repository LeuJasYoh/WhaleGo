using WhaleGo.Contracts;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Tests.TestInfrastructure;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform.Tests.Observation;

public sealed class UiaObservationTests
{
    [Fact]
    public void TreeContainsKnownControlsWithStableIndicesAndDepths()
    {
        using var harness = PlatformHarness.Start();
        var snapshot = harness.Observe();

        Assert.Equal("whalego-observe-v1", snapshot.SchemaVersion);
        Assert.Equal(ProtocolInfo.ObserveSchemaVersion, snapshot.SchemaVersion);
        Assert.NotEmpty(snapshot.Elements);
        Assert.Equal(WindowResolution.Exact, snapshot.Resolution);
        Assert.Equal(harness.WindowId, snapshot.Window.WindowId);
        Assert.Equal(snapshot.Window.AppId, snapshot.App.AppId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.App.Name));
        Assert.All(snapshot.Advisories, advisory => Assert.Contains(advisory, Advisories.All));

        // Stable sequential index + sane depth.
        var indices = snapshot.Elements.Select(e => e.Index).ToList();
        Assert.Equal(indices.Count, indices.Distinct().Count());
        Assert.Equal(indices.OrderBy(i => i).ToList(), indices);
        Assert.All(snapshot.Elements, e => Assert.InRange(e.Depth, 0, 64));

        var root = snapshot.Elements[0];
        Assert.Equal("window", root.Kind);
        Assert.Equal(0, root.Depth);

        var increment = Find(snapshot, TestFixtureForm.IncrementButtonText);
        Assert.Equal("button", increment.Kind);
        Assert.Contains("invoke", increment.Actions);
        Assert.True(increment.Enabled);
        Assert.True(increment.BoundsScreenDiag.Width > 0, $"bounds={increment.BoundsScreenDiag}");
        Assert.True(increment.Depth >= 1);
        Assert.Equal("btnIncrement", increment.AutomationId);
        Assert.Equal(TestFixtureForm.IncrementButtonText, increment.Title);

        var disabled = Find(snapshot, TestFixtureForm.DisabledButtonText);
        Assert.Equal("button", disabled.Kind);
        Assert.False(disabled.Enabled);

        var toggle = Find(snapshot, TestFixtureForm.ToggleCheckBoxText);
        Assert.Equal("checkbox", toggle.Kind);
        Assert.Contains("toggle", toggle.Actions);

        var input = Find(snapshot, TestFixtureForm.InputAccessibleName);
        Assert.Equal("edit", input.Kind);
        Assert.Equal(TestFixtureForm.InputInitialText, input.Value);

        Assert.Contains(snapshot.Elements, e => e.Kind == "listitem" && e.Title == "Alpha");
        Assert.Contains(snapshot.Elements, e => e.Kind == "listitem" && e.Title == "Beta");
        Assert.Contains(snapshot.Elements, e => e.Kind == "listitem" && e.Title == "Gamma");
        Assert.Contains(snapshot.Elements, e => e.Kind == "combobox");

        // Tree is fully contained: no budget/omission claims for this small window.
        Assert.Equal(0, snapshot.Omitted.OmittedCount);
        Assert.False(snapshot.Omitted.SparseIndices);
        Assert.Null(snapshot.Frame);
        Assert.DoesNotContain(Advisories.TreeBudgetExceeded, snapshot.Advisories);
    }

    [Fact]
    public void PasswordValueIsAlwaysMaskedAndPlaintextNeverAppears()
    {
        using var harness = PlatformHarness.Start();
        var snapshot = harness.Observe();

        var edits = snapshot.Elements.Where(e => e.Kind == "edit").ToList();
        Assert.True(edits.Count >= 2, Dump(snapshot));
        var masked = edits
            .Where(e => harness.Store.TryResolve(snapshot.StateId, e.Index, out var handle, out _) && handle!.IsPassword)
            .ToList();
        Assert.Single(masked);
        Assert.Equal(PrivacyRules.MaskedValue, masked[0].Value);
        Assert.False(masked[0].ValueTruncated);

        Assert.All(snapshot.Elements, e => Assert.DoesNotContain(TestFixtureForm.SecretPlaintext, e.Value ?? string.Empty));
        Assert.All(snapshot.Elements, e => Assert.DoesNotContain(TestFixtureForm.SecretPlaintext, e.Title ?? string.Empty));
    }

    [Fact]
    public void PasswordMaskingStaysOnEvenWhenConfigurationDisablesIt()
    {
        // Day-1 privacy rule is unconditional for IsPassword controls (PROTOCOL §11.1): the value is never read.
        using var harness = PlatformHarness.Start(maskPasswordFields: false);
        var snapshot = harness.Observe();
        var maskedElement = snapshot.Elements
            .Where(e => e.Kind == "edit")
            .Where(e => harness.Store.TryResolve(snapshot.StateId, e.Index, out var handle, out _) && handle!.IsPassword)
            .ToList();
        Assert.Single(maskedElement);
        Assert.Equal(PrivacyRules.MaskedValue, maskedElement[0].Value);
        Assert.All(snapshot.Elements, e => Assert.DoesNotContain(TestFixtureForm.SecretPlaintext, e.Value ?? string.Empty));
    }

    [Fact]
    public void MaxValueCharsControlsTruncationFlag()
    {
        using var harness = PlatformHarness.Start();

        var truncated = harness.Observe(maxValueChars: 10);
        var longNode = Find(truncated, TestFixtureForm.LongTextAccessibleName);
        Assert.NotNull(longNode.Value);
        Assert.Equal(10, longNode.Value!.Length);
        Assert.True(longNode.ValueTruncated);

        var full = harness.Observe(maxValueChars: 4000);
        var fullNode = Find(full, TestFixtureForm.LongTextAccessibleName);
        Assert.NotNull(fullNode.Value);
        Assert.Equal(300, fullNode.Value!.Length);
        Assert.False(fullNode.ValueTruncated);
        Assert.Equal(new string('x', 300), fullNode.Value);
    }

    [Fact]
    public void ElementCapShapesElementsAndReportsOmittedCounts()
    {
        using var harness = PlatformHarness.Start();
        var full = harness.Observe();
        var capped = harness.Observe(maxElements: 5);

        Assert.True(full.Elements.Count > 5, Dump(full));
        Assert.Equal(5, capped.Elements.Count);
        Assert.True(capped.Omitted.OmittedCount > 0);
        Assert.Equal("budget", capped.Omitted.Reason);
        Assert.True(capped.Omitted.SparseIndices);
        Assert.Contains(Advisories.TreeBudgetExceeded, capped.Advisories);
        // Indices stay the sequential prefix of the unshaped tree.
        Assert.Equal(full.Elements.Take(5).Select(e => e.Index), capped.Elements.Select(e => e.Index));
    }

    [Fact]
    public void ObserveWithScreenshotProducesActionableFrameAndKeepsHandles()
    {
        using var harness = PlatformHarness.Start();
        var snapshot = harness.Observe(includeScreenshot: true);

        Assert.NotNull(snapshot.Frame);
        var frame = snapshot.Frame!;
        Assert.Matches("^fr-[0-9a-f]{8}$", frame.FrameId);
        Assert.Equal(harness.WindowId, frame.WindowId);
        Assert.Equal("client", frame.CaptureRegion);
        Assert.Equal("printwindow", frame.CaptureMethod);
        Assert.Equal("nonblank_ok", frame.Signature);
        Assert.True(frame.Actionable, $"advisories={string.Join(',', snapshot.Advisories)}");
        Assert.NotNull(frame.PngBytes);
        Assert.True(frame.PngBytes!.Length > 100);
        Assert.Equal(0x89, frame.PngBytes[0]);
        Assert.Equal(frame.CapturedAtMs, snapshot.CapturedAtMs);
        Assert.DoesNotContain(Advisories.ScreenshotFailed, snapshot.Advisories);
        Assert.DoesNotContain(Advisories.CaptureDegraded, snapshot.Advisories);
        Assert.True(harness.Store.TryGetWindow(snapshot.StateId, out _));
    }

    [Fact]
    public void ScreenshotDisabledReportsAdvisoryWithoutFailingTree()
    {
        using var harness = PlatformHarness.Start(configureOptions: options => options.ScreenshotEnabled = false);
        var snapshot = harness.Observe(includeScreenshot: true);

        Assert.Null(snapshot.Frame);
        Assert.Contains(Advisories.ScreenshotDisabled, snapshot.Advisories);
        Assert.NotEmpty(snapshot.Elements);
    }

    [Fact]
    public void SensitiveWindowSuppressesScreenshotButKeepsTree()
    {
        var suffix = "bank-demo-" + Guid.NewGuid().ToString("N")[..8];
        using var harness = PlatformHarness.Start(suffix: suffix, sensitivePatterns: ["*bank*"]);
        var snapshot = harness.Observe(includeScreenshot: true);

        Assert.Null(snapshot.Frame);
        Assert.Contains(Advisories.ScreenshotSuppressed, snapshot.Advisories);
        Assert.DoesNotContain(Advisories.ScreenshotFailed, snapshot.Advisories);
        Assert.NotEmpty(snapshot.Elements);
        Assert.Contains(snapshot.Elements, e => e.Title == TestFixtureForm.IncrementButtonText);
    }

    [Fact]
    public void ResolvesAmbiguousAppByIdThrowingTypedError()
    {
        using var harness = PlatformHarness.Start();
        var secondSuffix = "second-" + harness.Suffix;
        using var second = StaFormHost.Start(form => TestFixtureForm.Configure(form, secondSuffix));
        _ = PlatformHarness.WaitForWindow(harness.Catalog, TestFixtureForm.BuildTitle(secondSuffix), TimeSpan.FromSeconds(20));

        var exception = Assert.Throws<ObservationPlatformException>(
            () => harness.Engine.Observe(new ObserveOptions(null, harness.Window.AppId, false, 0, 300, 48)));
        Assert.Equal(ErrorCodes.AmbiguousApp, exception.Code);
        Assert.True(exception.CandidateWindows.Count >= 2);
    }

    [Fact]
    public void ResolvesMissingWindowByIdThrowingTypedError()
    {
        using var harness = PlatformHarness.Start();
        var exception = Assert.Throws<ObservationPlatformException>(
            () => harness.Engine.Observe(new ObserveOptions("w-000000000001", null, false, 0, 300, 48)));
        Assert.Equal(ErrorCodes.WindowNotFound, exception.Code);
    }

    [Fact]
    public void TreeBuilderReportsUiaUnavailableForDeadHandle()
    {
        var engine = new UiaEngine();
        try
        {
            var builder = new UiaTreeBuilder(engine);
            var result = builder.Build(IntPtr.Zero, 48, true);
            Assert.True(result.UiaUnavailable);
            Assert.Empty(result.Nodes);
            Assert.False(result.BudgetExceeded);
        }
        finally
        {
            engine.Dispose();
        }
    }

    internal static ElementNode Find(ObservationSnapshot snapshot, string title) =>
        snapshot.Elements.FirstOrDefault(e => string.Equals(e.Title, title, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"Element with title '{title}' not found. Tree:\n{Dump(snapshot)}");

    internal static string Dump(ObservationSnapshot snapshot)
    {
        static string Quote(string? value) =>
            value is null ? "<null>" : value.Length <= 60 ? value : value[..60] + "…";
        return string.Join('\n', snapshot.Elements.Select(e =>
            $"#{e.Index} d{e.Depth} {e.Kind} title={Quote(e.Title)} value={Quote(e.Value)} auto={Quote(e.AutomationId)} actions=[{string.Join(',', e.Actions)}] enabled={e.Enabled} selected={e.Selected}"));
    }
}
