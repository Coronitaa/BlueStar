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
        if (instance.Engine?.Type == EngineType.Unity)
        {
            var unity = _managers.FirstOrDefault(m => m.Id == "unity");
            if (unity != null && unity.IsSupported(instance)) return unity;
        }

        if (instance.Engine?.Type == EngineType.UnrealEngine)
        {
            var unreal = _managers.FirstOrDefault(m => m.Id == "unreal-paks");
            if (unreal != null && unreal.IsSupported(instance)) return unreal;
        }

        // Fallback to other specialized managers
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
