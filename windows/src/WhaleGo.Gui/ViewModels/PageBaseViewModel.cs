using System.Threading.Tasks;
using WhaleGo.Gui.Infrastructure;

namespace WhaleGo.Gui.ViewModels;

/// <summary>Base for the six settings pages (presenter side). Views bind to these objects.</summary>
public abstract class PageBaseViewModel : ObservableObject
{
    protected PageBaseViewModel(string title, string navLabel, string description, GuiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Title = title;
        NavLabel = navLabel;
        Description = description;
        Context = context;
    }

    public string Title { get; }

    public string NavLabel { get; }

    public string Description { get; }

    public GuiContext Context { get; }

    public virtual bool SupportsApply => false;

    /// <summary>Called when the page becomes visible or when the user presses refresh.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>Called on every status poll while the page is visible.</summary>
    public virtual void OnStatusUpdated(WhaleGo.Contracts.Model.StatusSnapshot snapshot)
    {
    }
}
