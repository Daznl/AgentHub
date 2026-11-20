using System.Globalization;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// A fraction as a whole percentage, written "55%" whatever the computer's culture (the invariant culture, which Linux
/// services often run in, would write "55 %"). Same rounding as the "P0" format.
/// </summary>
internal static class Percent
{
    private static readonly NumberFormatInfo Format = new() { PercentPositivePattern = 1, PercentNegativePattern = 1 };

    public static string Of(double fraction) => fraction.ToString("P0", Format);
}
