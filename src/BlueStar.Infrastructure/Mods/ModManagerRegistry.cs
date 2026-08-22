using System;
using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Registry containing available mod managers, selecting the best match for any instance.
/// </summary>
public sealed class ModManagerRegistry : IModManagerRegistry
{
    private readonly List<IModManager> _managers;

    public ModManagerRegistry(IEnumerable<IModManager> managers)
    {
        _managers = managers.ToList();
    }

    public IModManager? GetManagerForInstance(GameInstance instance)
    {
        // First check non-generic managers
        var specialized = _managers
            .Where(m => m.Id != "generic")
            .FirstOrDefault(m => m.IsSupported(instance));

        if (specialized != null) return specialized;

        // Fallback to generic if instance supports mods
        if (instance.Engine?.Supports(EngineCapabilities.Mods) == true)
        {
            return _managers.FirstOrDefault(m => m.Id == "generic");
        }

        return _managers.FirstOrDefault(m => m.IsSupported(instance));
    }

    public IReadOnlyList<IModManager> GetAllManagers() => _managers.AsReadOnly();
}
