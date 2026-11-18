using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glint;

/// <summary>
/// Currency conversion: "100 usd to lkr", "$50 in eur", "€20" (to the home currency).
/// Rates come from open.er-api.com (free, no key, updated daily) and are cached on disk for offline use.
/// </summary>
internal static class Currency
{
    const string Url = "https://open.er-api.com/v6/latest/USD";
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    static readonly string CachePath = Path.Combine(Settings.Dir, "rates.json");

    static volatile Dictionary<string, double> _rates = new();   // units of currency per 1 USD
    static DateTime _ratesDate;                                   // when the provider published them
    static DateTime _fetched = DateTime.MinValue;                 // when we last downloaded
    static int _refreshing;

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD", ["bucks"] = "USD",
        ["€"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR",
        ["£"] = "GBP", ["pound"] = "GBP", ["pounds"] = "GBP", ["quid"] = "GBP", ["sterling"] = "GBP",
        ["¥"] = "JPY", ["yen"] = "JPY",
        ["₹"] = "INR", ["yuan"] = "CNY", ["rmb"] = "CNY", ["won"] = "KRW", ["₩"] = "KRW",
        ["franc"] = "CHF", ["francs"] = "CHF", ["dirham"] = "AED", ["dirhams"] = "AED",
        ["riyal"] = "SAR", ["ringgit"] = "MYR", ["baht"] = "THB", ["peso"] = "MXN", ["pesos"] = "MXN",
        ["ruble"] = "RUB", ["rubles"] = "RUB", ["rand"] = "ZAR", ["real"] = "BRL", ["lira"] = "TRY",
    };

    /// <summary>Codes accepted without an explicit target ("100 usd"), to avoid matching ordinary words.</summary>
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "USD", "EUR", "GBP", "JPY", "LKR", "INR", "AUD", "CAD", "CHF", "CNY", "SGD", "AED", "NZD", "KRW",
        "HKD", "SEK", "NOK", "DKK", "MYR", "THB", "PKR", "BDT", "SAR", "QAR", "ZAR", "BRL", "MXN", "RUB",
    };

    static readonly Regex Split = new(
        @"^\s*(?<a>.+?)(?:(?:\s+(?:to|in|as|into)\s+|\s*(?:=|->|→)\s*)(?<b>.+?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Amount = new(
        @"^(?<pre>[^\d.]*?)\s*(?<n>\d[\d,]*\.?\d*|\.\d+)?\s*(?<post>[^\d]*)$", RegexOptions.Compiled);

    public readonly record struct Conversion(string Result, string Description);

    public static void EnsureFresh()
    {
        if (_rates.Count == 0) LoadCache();
        if (DateTime.UtcNow - _fetched < MaxAge) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string json = await http.GetStringAsync(Url);
                if (Parse(json))
                {
                    _fetched = DateTime.UtcNow;
                    Directory.CreateDirectory(Settings.Dir);
                    File.WriteAllText(CachePath, json);
                }
            }
            catch
            {
                _fetched = DateTime.UtcNow - MaxAge + TimeSpan.FromMinutes(5); // offline: retry in 5 minutes
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    static void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            if (Parse(File.ReadAllText(CachePath))) _fetched = File.GetLastWriteTimeUtc(CachePath);
        }
        catch { }
    }

    static bool Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("rates", out var rates)) return false;

        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rates.EnumerateObject())
            if (p.Value.TryGetDouble(out double v) && v > 0) dict[p.Name] = v;
        if (dict.Count == 0) return false;

        _rates = dict;
        _ratesDate = root.TryGetProperty("time_last_update_unix", out var t) && t.TryGetInt64(out long unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
            : DateTime.Now;
        return true;
    }

    public static Conversion? TryConvert(string query)
    {
        var rates = _rates;
        if (rates.Count == 0) return null;

        var m = Split.Match(query);
        if (!m.Success) return null;
        var a = Amount.Match(m.Groups["a"].Value.Trim());
        if (!a.Success) return null;

        string pre = a.Groups["pre"].Value.Trim(), post = a.Groups["post"].Value.Trim();
        if (pre.Length > 0 && post.Length > 0) return null;
        string fromToken = pre.Length > 0 ? pre : post;
        bool hasAmount = a.Groups["n"].Success;
        bool hasTarget = m.Groups["b"].Success;
        if (!hasAmount && !hasTarget) return null; // a bare "usd" is not a conversion

        string? from = Resolve(fromToken, rates, strict: !hasTarget);
        if (from == null) return null;

        string? to;
        if (hasTarget)
        {
            to = Resolve(m.Groups["b"].Value.Trim(), rates, strict: false);
            if (to == null) return null;
        }
        else
        {
            to = Settings.HomeCurrency;
            if (!rates.ContainsKey(to)) to = "USD";
            if (to == from) to = from == "USD" ? "EUR" : "USD";
        }
        if (to == from) return null;

        double amount = 1;
        if (hasAmount && !double.TryParse(a.Groups["n"].Value.Replace(",", ""), NumberStyles.Float,
                CultureInfo.InvariantCulture, out amount))
            return null;

        double rate = rates[to] / rates[from];
        return new Conversion(
            $"{Money(amount * rate)} {to}",
            $"{Calculator.Format(amount)} {from}  ·  1 {from} = {Money(rate)} {to}  ·  rates from {_ratesDate:d MMM}");
    }

    static string? Resolve(string token, Dictionary<string, double> rates, bool strict)
    {
        if (token.Length == 0) return null;
        if (Aliases.TryGetValue(token, out var code)) return rates.ContainsKey(code) ? code : null;
        if (token.Equals("rs", StringComparison.OrdinalIgnoreCase) || token.Equals("rupee", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("rupees", StringComparison.OrdinalIgnoreCase))
        {
            // "rupees" means the local rupee where there is one, otherwise the Indian rupee
            string home = Settings.HomeCurrency;
            return home is "LKR" or "PKR" or "NPR" or "INR" or "MUR" or "SCR" ? home : "INR";
        }
        if (token.Length != 3 || !token.All(char.IsLetter) || !rates.ContainsKey(token)) return null;
        string upper = token.ToUpperInvariant();
        if (strict && !Common.Contains(upper) && upper != Settings.HomeCurrency) return null;
        return upper;
    }

    static string Money(double v)
    {
        double a = Math.Abs(v);
        string fmt = a >= 1 || a == 0 ? "#,##0.00" : a >= 0.01 ? "0.0000" : "0.00000000";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
