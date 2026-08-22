using System.Collections.Generic;
using System.Linq;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Registry managing all available emulator providers.
/// </summary>
public sealed class EmulatorRegistry : IEmulatorRegistry
{
    private readonly List<IEmulator> _emulators;

    public EmulatorRegistry(IEnumerable<IEmulator> emulators)
    {
        _emulators = emulators.ToList();
    }

    public IEmulator? GetById(string id) =>
        _emulators.FirstOrDefault(e => string.Equals(e.Id, id, System.StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<IEmulator> GetSupportedEmulators(GameInstance instance) =>
        _emulators.Where(e => e.IsSupported(instance)).ToList().AsReadOnly();

    public IReadOnlyList<IEmulator> GetAllEmulators() => _emulators.AsReadOnly();
}
