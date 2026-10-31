namespace WhaleGo.Gui.ViewModels.Fields;

/// <summary>A titled card containing related config rows (drives the shared ConfigForm view).</summary>
public sealed record ConfigFieldGroup(string Title, string? Description, IReadOnlyList<ConfigField> Fields);
