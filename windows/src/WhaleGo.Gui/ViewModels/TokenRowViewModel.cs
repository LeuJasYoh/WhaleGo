using System.Windows.Input;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Validation;

namespace WhaleGo.Gui.ViewModels;

/// <summary>One token list row: masked value only, plus copy / rotate affordances (docs/GUI.md 7.2).</summary>
public sealed class TokenRowViewModel
{
    public TokenRowViewModel(TokenDescriptor descriptor, ICommand copyCommand, ICommand rotateCommand)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        CopyCommand = copyCommand;
        RotateCommand = rotateCommand;
    }

    public TokenDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;

    public string Masked => TokenMasker.Display(Descriptor);

    public string CreatedAt => string.IsNullOrWhiteSpace(Descriptor.CreatedAt) ? "未知" : Descriptor.CreatedAt;

    public bool Available => Descriptor.Available;

    public string AvailabilityText => Available ? "可用" : "不可读";

    public ICommand CopyCommand { get; }

    public ICommand RotateCommand { get; }
}
