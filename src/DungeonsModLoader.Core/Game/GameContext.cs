namespace DungeonsModLoader.Core.Game;

/// <inheritdoc cref="IGameContext"/>
public sealed class GameContext : IGameContext
{
    private readonly object _gate = new();
    private GameInstallation? _current;

    public GameInstallation? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsConfigured => Current is not null;

    public event EventHandler? Changed;

    public void Set(GameInstallation? installation)
    {
        lock (_gate)
        {
            if (Equals(_current, installation))
            {
                return;
            }

            _current = installation;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
