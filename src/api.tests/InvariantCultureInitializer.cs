using System.Globalization;
using System.Runtime.CompilerServices;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Pins the culture for the whole test run. Several assertions match formatted numbers and
/// dates ("50.0", "1 September 2026"), which differ under a contributor's own locale.
/// </summary>
internal static class InvariantCultureInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
