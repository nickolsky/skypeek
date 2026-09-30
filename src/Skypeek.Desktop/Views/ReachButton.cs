using Avalonia.Controls;
using FluentIcons.Common;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>"Check reach…" on a resource's details (the DataContext is the resource): opens Analyze reach from it.</summary>
public sealed class ReachButton : IconButton
{
    public ReachButton()
    {
        Symbol = Symbol.PlugConnected;
        Content = "Check reach…";
        ToolTip.SetTip(this, "Can this reach another server, database or IP (security groups, network ACLs, routes)?");
        Click += (_, _) =>
        {
            if (DataContext is ResourceStatus r && TopLevel.GetTopLevel(this) is MainWindow main)
                main.OpenReach(r.ResourceKey);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        IsVisible = DataContext is Ec2InstanceStatus or RdsInstanceStatus or RdsClusterStatus or CacheStatus or LoadBalancerStatus
            or RedshiftStatus or EcsServiceStatus or EbEnvironmentStatus;
    }
}
