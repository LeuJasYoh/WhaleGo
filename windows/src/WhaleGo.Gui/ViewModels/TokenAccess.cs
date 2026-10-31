using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels;

/// <summary>Availability rules for token display / "copy plugin config" (docs/GUI.md 5.2 / 7.2).</summary>
public static class TokenAccess
{
    public static bool AnyAvailable(IReadOnlyList<TokenDescriptor>? tokens) =>
        tokens is not null && tokens.Any(static token => token.Available);

    /// <summary>"复制插件配置" needs both an available token and a readable name.</summary>
    public static bool CanCopyPluginConfig(IReadOnlyList<TokenDescriptor>? tokens) =>
        tokens is not null && tokens.Any(static token => token.Available && !string.IsNullOrWhiteSpace(token.Name));

    public static TokenDescriptor? Primary(IReadOnlyList<TokenDescriptor>? tokens) =>
        tokens?.FirstOrDefault(static token => token.Available) ?? tokens?.FirstOrDefault();

    public static string Summary(IReadOnlyList<TokenDescriptor>? tokens)
    {
        if (tokens is null || tokens.Count == 0)
        {
            return "尚未生成 token";
        }

        return tokens.Count == 1
            ? "1 个 token · " + TokenMasker.Display(tokens[0])
            : tokens.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个 token";
    }
}
