using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Decides where the files of a staged mod belong inside a game install, without a per-game address book:
/// provider layout hints first, then aligning the archive with the existing folder tree, then engine and
/// file-signature rules, and finally a low-confidence fallback that asks the user.
/// </summary>
public interface IModPlacementResolver
{
    /// <summary>Builds a plan for the files in <paramref name="stagingDirectory"/>. Nothing is written.</summary>
    ModInstallPlan Resolve(GameInstance instance, string stagingDirectory, ModPlacementHints hints);
}
