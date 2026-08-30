using System.ComponentModel;
using System.Reflection;
using System.Text;
using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Application.Hosting;

/// <summary>
/// Reads every configuration value the app binds, before anything is built, so a
/// mistyped one fails as a sentence an operator can act on.
/// <para>
/// The framework's own answer is an unhandled
/// <c>Failed to convert configuration value '1' at 'Auth:Oidc:Enabled' to type
/// 'System.Boolean'.</c> and a stack trace. It names the setting in its internal
/// colon form rather than the <c>Auth__Oidc__Enabled</c> the operator actually
/// typed, and it never says what a valid value would have been — which for a
/// <c>bool</c> is the whole question, because .NET accepts only <c>true</c> and
/// <c>false</c> while Docker convention is <c>1</c> and <c>0</c> (#198).
/// </para>
/// <para>
/// Same reasoning as <see cref="AppRuntimeMode.Parse"/> one file over: a
/// configuration mistake should cost one obvious crash. The difference this
/// makes is only in the message — every value rejected here is a value the
/// binder rejects too, checked with the same <see cref="TypeConverter"/> it
/// uses.
/// </para>
/// <para>
/// Two things are deliberately out of reach. A mistyped variable <em>name</em>
/// binds nothing, so there is no value to check and the default applies in
/// silence — the oldest trap on this deployment surface, and one only the
/// documentation can warn about. And variables the framework or another SDK
/// reads rather than this app (<c>ASPNETCORE_*</c>, <c>Logging__*</c>,
/// <c>OTEL_*</c>) keep their own rules; telemetry falls back on an unrecognised
/// value on purpose, because a typo in a tracing variable must not be why a
/// mail-ingesting service refuses to boot.
/// </para>
/// <para>
/// What it does change is <em>when</em>. <c>Services.Configure&lt;T&gt;</c> binds
/// lazily, so a bad <c>Alerts__IntervalMinutes</c> used to surface as a 500 from
/// whichever request first resolved <c>IOptions&lt;AlertOptions&gt;</c>, long
/// after the container looked healthy. Every section is checked here, in every
/// mode, because the deployment contract is that one set of variables means the
/// same thing everywhere — so a typo an <c>api</c> pod would reject should not
/// let the <c>migrate</c> Job start.
/// </para>
/// </summary>
public static class ConfigurationPreflight
{
    /// <summary>
    /// Config sections bound to a strongly-typed options class. Adding a section
    /// without listing it here leaves its settings unchecked;
    /// <c>ConfigurationContractTests</c> reads this array and fails the build if
    /// an options class is missing from it.
    /// </summary>
    public static readonly (string Section, Type Type)[] BoundSections =
    [
        ("Worker", typeof(DmarcAnalyzer.Api.Workers.WorkerOptions)),
        ("Email", typeof(DmarcAnalyzer.Api.Application.Notifications.EmailOptions)),
        ("Alerts", typeof(DmarcAnalyzer.Api.Application.Notifications.AlertOptions)),
        ("Digest", typeof(DmarcAnalyzer.Api.Application.Notifications.DigestOptions)),
        ("Dns", typeof(DmarcAnalyzer.Api.Application.Analytics.DnsOptions)),
        ("MtaSts", typeof(DmarcAnalyzer.Api.Application.MtaSts.MtaStsOptions)),
        ("Retention", typeof(DmarcAnalyzer.Api.Application.Retention.RetentionOptions)),
        ("Network", typeof(DmarcAnalyzer.Api.Application.Security.NetworkOptions)),
        ("Backup", typeof(DmarcAnalyzer.Api.Application.Backup.BackupOptions)),
        ("Auth:Oidc", typeof(OidcOptions)),
    ];

    /// <summary>
    /// Typed settings read straight from <see cref="IConfiguration"/>, which have
    /// no options class to reflect over. <c>APP_MODE</c> is absent deliberately —
    /// <see cref="AppRuntimeMode"/> already refuses a bad one with a better
    /// message than anything here could produce.
    /// </summary>
    private static readonly (string Key, Type Type)[] LooseSettings =
    [
        ("Database:MigrateOnStartup", typeof(bool)),
    ];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming the first setting
    /// whose value cannot be converted to the type it is bound to. Returns
    /// quietly when everything converts, including when nothing is set at all.
    /// </summary>
    public static void Validate(IConfiguration configuration)
    {
        foreach (var (sectionName, type) in BoundSections)
        {
            var section = configuration.GetSection(sectionName);

            try
            {
                section.Get(type);
            }
            catch (InvalidOperationException original)
            {
                // The binder has already decided this section is bad; the walk
                // below only works out which key to blame.
                throw Explain(section, type, original);
            }
        }

        foreach (var (key, type) in LooseSettings)
        {
            var raw = configuration[key];

            if (raw is not null && !Converts(type, raw))
            {
                throw Failure(key, type, raw);
            }
        }
    }

    /// <summary>
    /// The environment variable an operator sets for a colon-separated
    /// configuration path: <c>Auth:Oidc:Enabled</c> is <c>Auth__Oidc__Enabled</c>.
    /// </summary>
    public static string EnvironmentVariableName(string configurationPath)
        => configurationPath.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// Finds the offending key by re-running the binder's own conversion over
    /// each settable property. Falls back to the framework's exception when the
    /// bad value is somewhere a flat walk cannot see — inside a list, or a
    /// nested object — because a wrong name would be worse than a terse one.
    /// </summary>
    private static Exception Explain(IConfigurationSection section, Type type, InvalidOperationException original)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetSetMethod() is null)
            {
                continue; // computed, not bindable
            }

            // Null means unset; empty means set to nothing, which the binder
            // rejects for every non-string type and is worth naming.
            var raw = section[property.Name];

            if (raw is null || Converts(property.PropertyType, raw))
            {
                continue;
            }

            return Failure($"{section.Path}:{property.Name}", property.PropertyType, raw, original);
        }

        return original;
    }

    /// <summary>
    /// Whether the configuration binder would accept this string for this type.
    /// Deliberately the same <see cref="TypeDescriptor"/> lookup
    /// <c>ConfigurationBinder</c> performs, so the two cannot disagree.
    /// </summary>
    private static bool Converts(Type type, string raw)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
        {
            return true;
        }

        var converter = TypeDescriptor.GetConverter(target);

        if (!converter.CanConvertFrom(typeof(string)))
        {
            // Arrays, lists and nested objects are bound element by element
            // rather than converted; nothing to check at this level.
            return true;
        }

        try
        {
            converter.ConvertFromInvariantString(raw);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static InvalidOperationException Failure(
        string configurationPath, Type type, string raw, Exception? inner = null)
    {
        var message = new StringBuilder()
            .Append(EnvironmentVariableName(configurationPath))
            .Append("='")
            .Append(raw)
            .Append("' is not a valid value. Expected ")
            .Append(Describe(type))
            .Append('.');

        if (raw.Length == 0)
        {
            // An operator who writes `Auth__Oidc__Enabled=` in an env file has
            // set the variable, not left it out, and the binder does not treat
            // the two the same. Nothing else in the message would say so.
            message.Append(" The variable is set to an empty value, which is not the ")
                .Append("same as leaving it unset; remove it to take the default.");
        }

        if (LooksQuoted(raw))
        {
            // Worth saying, because the value looks right and the error does not.
            // A Compose `environment:` list entry and a `docker run --env-file`
            // line both keep the quote characters; Compose's mapping form and
            // `env_file:` strip them, which is why the same value works in one
            // place and not another.
            message.Append(" The quote characters are part of the value here — ")
                .Append("a Compose `environment:` list entry (`- NAME=\"true\"`) and a ")
                .Append("`docker run --env-file` line both keep them, unlike Compose's ")
                .Append("mapping form (`NAME: \"true\"`) and `env_file:`.");
        }

        message.Append(" See docs/ops/configuration.md.");

        return new InvalidOperationException(message.ToString(), inner);
    }

    private static string Describe(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(bool))
        {
            return "true or false — 1, 0, yes, no, on and off are not accepted";
        }

        if (target == typeof(int) || target == typeof(long)
            || target == typeof(short) || target == typeof(byte)
            || target == typeof(uint) || target == typeof(ulong))
        {
            return "a whole number";
        }

        if (target == typeof(double) || target == typeof(float) || target == typeof(decimal))
        {
            return "a number";
        }

        if (target.IsEnum)
        {
            return $"one of: {string.Join(", ", Enum.GetNames(target))}";
        }

        return $"a value of type {target.Name}";
    }

    private static bool LooksQuoted(string raw)
        => raw.Length >= 2
            && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\''));
}
