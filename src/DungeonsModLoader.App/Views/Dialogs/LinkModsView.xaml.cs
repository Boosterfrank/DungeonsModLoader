using System.Windows.Controls;

namespace DungeonsModLoader.App.Views.Dialogs;

/// <summary>Body of the "Link mods to Nexus Mods" dialog and of the wizard's linking step. DataContext: <see cref="ViewModels.Dialogs.LinkModsViewModel"/>.</summary>
public partial class LinkModsView : UserControl
{
    public LinkModsView()
    {
        InitializeComponent();
    }
}
