namespace RingMouse.Core.Config;

/// <summary>Durchsucht eine Config nach Aktionen mit Nebenwirkungen – z.B. für eine Warnung vor dem Import.</summary>
public static class ConfigInspection
{
    /// <summary>Anzahl ausführender Aktionen (Programmstart, PowerShell) und ob eine davon Adminrechte anfordert.</summary>
    public readonly record struct ExecutableActions(int Launch, int PowerShell, bool AnyElevated)
    {
        public int Total => Launch + PowerShell;
    }

    /// <summary>Alle Aktionen aus Tasten, Ringen und Profilen – Sequenzen rekursiv aufgelöst.</summary>
    public static IEnumerable<ActionDefinition> EnumerateActions(RingMouseConfig config)
    {
        foreach (var action in config.Buttons.Values)
            foreach (var flat in Flatten(action))
                yield return flat;

        foreach (var ring in config.Rings.Values)
            foreach (var segment in ring.Segments)
                if (segment?.Action is { } action)
                    foreach (var flat in Flatten(action))
                        yield return flat;

        foreach (var profile in config.Profiles)
            foreach (var action in profile.Buttons.Values)
                foreach (var flat in Flatten(action))
                    yield return flat;
    }

    private static IEnumerable<ActionDefinition> Flatten(ActionDefinition action)
    {
        yield return action;
        if (action is SequenceAction seq)
            foreach (var step in seq.Steps)
                foreach (var flat in Flatten(step))
                    yield return flat;
    }

    /// <summary>Zählt Aktionen, die beim Auslösen Programme/Befehle ausführen (Launch, PowerShell).</summary>
    public static ExecutableActions CountExecutable(RingMouseConfig config)
    {
        var launch = 0;
        var powershell = 0;
        var elevated = false;
        foreach (var action in EnumerateActions(config))
        {
            switch (action)
            {
                case LaunchAction l:
                    launch++;
                    elevated |= l.Elevated;
                    break;
                case PowerShellAction ps:
                    powershell++;
                    elevated |= ps.Elevated;
                    break;
            }
        }
        return new ExecutableActions(launch, powershell, elevated);
    }
}
