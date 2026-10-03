using DungeonsModLoader.Core.Game;

namespace DungeonsModLoader.Core.Tests.Mods;

/// <summary>Minimal <see cref="IGameContext"/> for tests: raises Changed on every Set.</summary>
internal sealed class FakeGameContext : IGameContext
{
    public FakeGameContext(GameInstallation? current = null)
    {
        Current = current;
    }

    public GameInstallation? Current { get; private set; }

    public bool IsConfigured => Current is not null;

    public event EventHandler? Changed;

    public void Set(GameInstallation? installation)
    {
        Current = installation;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
