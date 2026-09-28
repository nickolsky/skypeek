using Skypeek.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Skypeek.App.Infrastructure;

/// <summary>Windows toast notifications. While the UI is locked only the title (target + resource) is shown.</summary>
public sealed class ToastNotifier(Func<bool> isLocked) : INotifier
{
    public void Notify(string title, string message, string? detail = null)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("action", "dashboard")
                .AddText(title);
            if (isLocked())
            {
                builder.AddText($"Details hidden while {AppInfo.Name} is locked.");
            }
            else
            {
                builder.AddText(message);
                if (detail is not null)
                    builder.AddText(detail);
            }
            builder.Show();
        }
        catch
        {
            // Notifications can be disabled by the user or policy.
        }
    }
}
