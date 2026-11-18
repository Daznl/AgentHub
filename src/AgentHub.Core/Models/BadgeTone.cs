namespace AgentHub.Models;

/// <summary>
/// The colour a repository badge or button is drawn in, as a meaning rather than a colour, so the models carry no UI
/// types. The Windows app turns it into brushes (<c>Controls/BadgeToneBrushConverter</c>); a text front end can ignore it.
/// </summary>
public enum BadgeTone
{
    UpToDate,
    Behind,
    Ahead,
    Dirty,
    NotCloned,
    Missing,
    Private,
    Public,
    AddedButton,
    CloneButton,
}
