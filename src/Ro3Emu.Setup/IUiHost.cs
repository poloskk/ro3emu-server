namespace Ro3Emu.Setup;

/// <summary>The kind of a <see cref="Ui"/> line.</summary>
internal enum UiLine { Title, Step, Info, Ok, Warn, Error }

/// <summary>
/// A window that shows what the console would (Ui.Host): the launcher window (Ro3Emu.Play) runs the same flows in
/// process. Questions block the flow's thread until answered; keys are the console's (S, Q, R).
/// </summary>
internal interface IUiHost
{
    void Line(UiLine kind, string text);

    /// <summary>The status line; <paramref name="fraction"/> (0-1) when the work's share is known.</summary>
    void Progress(string text, double? fraction);

    void EndProgress();

    bool Confirm(string question, bool defaultYes);

    string Ask(string question, string defaultValue);

    /// <summary>A key pressed in the window since the last call, or null.</summary>
    ConsoleKey? TakeKey();

    /// <summary>Where the flow is: "client", "data", "server", "patch", "bots", "running", "stopping", "done".</summary>
    void Phase(string phase);

    /// <summary>Fake players online out of those started (PopulationProcess).</summary>
    void Bots(int online, int total);
}
