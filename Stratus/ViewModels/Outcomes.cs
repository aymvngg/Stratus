using Microsoft.UI.Xaml.Controls;
using Stratus.Core.Coordination;

namespace Stratus.ViewModels;

public static class Outcomes
{
    public static InfoBarSeverity ToInfoBarSeverity(this OutcomeSeverity severity) => severity switch
    {
        OutcomeSeverity.Success => InfoBarSeverity.Success,
        OutcomeSeverity.Warning => InfoBarSeverity.Warning,
        OutcomeSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
}
