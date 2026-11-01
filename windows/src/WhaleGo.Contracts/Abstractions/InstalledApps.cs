using WhaleGo.Contracts.Model;

namespace WhaleGo.Contracts.Abstractions;

/// <summary>
/// Resolves a model-provided app name (Chinese/English display name, exe name, or absolute path)
/// to a concrete launch target. Implemented by the platform layer; the application layer owns the
/// APP_NOT_FOUND/AMBIGUOUS_APP mapping and audit.
/// </summary>
public interface IInstalledAppCatalog
{
    InstalledAppResolveResult Resolve(string name);
}
