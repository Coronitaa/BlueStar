namespace BlueStar.App.ViewModels;

/// <summary>
/// Marks a ViewModel that outlives the view showing it.
/// </summary>
/// <remarks>
/// Most tabs build a fresh ViewModel each time they are opened and throw it away on the way out.
/// Explore cannot afford that: its filter panel is assembled from the live Steam tag catalog and
/// its results are paid for one rate-limited request at a time, so rebuilding it on every tab
/// switch both froze the window and threw away the person's filters. A ViewModel marked here is
/// resolved once and kept, and navigation leaves its lifetime alone.
/// </remarks>
public interface ISharedViewModel
{
}
