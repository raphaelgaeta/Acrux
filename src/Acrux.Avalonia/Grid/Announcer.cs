using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Acrux;

/// <summary>
/// Speech for what the grid draws but no control holds: the current cell, the
/// selection, a copy, a filter (design approved 2026-11-15). Avalonia 12 has no
/// table pattern for screen readers, so the grid speaks through a live region,
/// the idea of WAI-ARIA's aria-live: an element whose name, when it changes, the
/// screen reader says, wherever the focus is. On Windows Avalonia turns that
/// change into UI Automation's LiveRegionChanged (Narrator, NVDA); on Linux it
/// sends the name change over AT-SPI (Orca). It draws nothing and takes no
/// click: one pixel, so it is still "on screen" to the reader.
/// </summary>
internal sealed class Announcer : Control
{
    private const char Repeat = '\u200B';   // a zero-width space: no reader says it, but the name changes
    private string _text = "";

    /// <param name="interrupt">
    /// Assertive cuts off what is being said (a move: the newest cell is the
    /// one that matters); polite waits its turn (a copy, a filter).
    /// </param>
    public Announcer(bool interrupt)
    {
        AutomationProperties.SetLiveSetting(this, interrupt ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        Width = Height = 1;
        IsHitTestVisible = false;
        Focusable = false;
    }

    public void Say(string text)
    {
        // the same words twice are heard twice: a name that doesn't change raises nothing,
        // so a repeat alternates a zero-width space at the end
        if (text == _text.TrimEnd(Repeat))
            text = _text.EndsWith(Repeat) ? text : text + Repeat;
        string old = _text;
        _text = text;
        try
        {
            ControlAutomationPeer.CreatePeerForElement(this).RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, text);
        }
        catch (Exception)
        {
            // speech is never worth the app: a reader that can't be told this time hears the next one
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(Announcer owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() => owner._text;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override AutomationLiveSetting GetLiveSettingCore() => AutomationProperties.GetLiveSetting(owner);
    }
}
