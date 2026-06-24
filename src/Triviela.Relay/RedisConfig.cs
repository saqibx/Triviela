using StackExchange.Redis;

namespace Triviela.Relay;

/// <summary>
/// Normalises a Redis connection string into <see cref="ConfigurationOptions"/>. Accepts both the
/// URI form that Upstash (and most hosts) display — <c>rediss://default:password@host:6379</c> —
/// which StackExchange.Redis does NOT parse natively, and StackExchange's own
/// <c>host:port,password=...,ssl=True</c> form. So you can paste whichever the dashboard gives you.
/// </summary>
public static class RedisConfig
{
    public static ConfigurationOptions Parse(string connectionString)
    {
        var value = connectionString.Trim();

        if (value.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(value);
            var options = new ConfigurationOptions
            {
                AbortOnConnectFail = false,
                Ssl = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase),
            };
            options.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                if (parts.Length == 2)
                {
                    // "default" is Redis' implicit user — leave User unset so plain AUTH is used.
                    if (!parts[0].Equals("default", StringComparison.OrdinalIgnoreCase))
                        options.User = Uri.UnescapeDataString(parts[0]);
                    options.Password = Uri.UnescapeDataString(parts[1]);
                }
                else
                {
                    options.Password = Uri.UnescapeDataString(parts[0]);
                }
            }
            return options;
        }

        var parsed = ConfigurationOptions.Parse(value);
        parsed.AbortOnConnectFail = false;
        return parsed;
    }
}
