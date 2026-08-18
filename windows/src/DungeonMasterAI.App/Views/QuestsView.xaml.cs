using System.Windows.Controls;

namespace DungeonMasterAI.App.Views;

/// <summary>
/// Quest journal, factions, secrets, and world timeline.
///
/// The shell's Quests tab previously hosted a second <see cref="WorldView"/> instance, which made
/// the tab a duplicate of World and left quest objectives, DM notes, secret truths, and timeline
/// consequences unreachable anywhere in the shell.
/// </summary>
public partial class QuestsView : UserControl
{
    public QuestsView() => InitializeComponent();
}
