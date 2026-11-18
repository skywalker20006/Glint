using System.Globalization;
using System.Text.RegularExpressions;

namespace Glint;

/// <summary>Measurement conversions: "5 km to mi", "72 f in c", or just "12 kg" for the usual counterpart.</summary>
internal static class Units
{
    sealed record Unit(string Category, string Symbol, string Name, double Factor, double Offset);

    static readonly Dictionary<string, Unit> ByAlias = new();
    static readonly Dictionary<string, string> Defaults = new();

    const string Num = @"(?<n>[-+]?(?:\d[\d,]*\.?\d*|\.\d+))";
    static readonly Regex Full = new(
        "^" + Num + @"\s*(?<from>[^\d\s].*?)(?:\s+(?:to|in|as|into)\s+|\s*(?:=|->|→)\s*)(?<to>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Alone = new("^" + Num + @"\s*(?<from>[^\d\s].*)$", RegexOptions.Compiled);

    public readonly record struct Conversion(string Result, string Description);

    public static Conversion? TryConvert(string query)
    {
        var m = Full.Match(query);
        Unit? from, to;
        if (m.Success)
        {
            from = Find(m.Groups["from"].Value);
            to = Find(m.Groups["to"].Value);
        }
        else
        {
            m = Alone.Match(query);
            if (!m.Success) return null;
            from = Find(m.Groups["from"].Value);
            to = from != null && Defaults.TryGetValue(from.Symbol, out var d) ? ByAlias[Normalize(d)] : null;
        }
        if (from == null || to == null || from.Category != to.Category || ReferenceEquals(from, to)) return null;
        if (!double.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return null;

        double baseValue = (value + from.Offset) * from.Factor;
        double result = baseValue / to.Factor - to.Offset;

        return new Conversion(
            $"{FormatValue(result)} {to.Symbol}",
            $"{Calculator.Format(value)} {from.Name} in {to.Name}");
    }

    static string FormatValue(double v)
    {
        double a = Math.Abs(v);
        if (a != 0 && (a >= 1e15 || a < 1e-4)) return v.ToString("0.####E+0", CultureInfo.InvariantCulture);
        // 6 significant digits is plenty for everyday conversions
        double rounded = double.Parse(v.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return rounded.ToString("#,##0.######", CultureInfo.InvariantCulture);
    }

    static Unit? Find(string text) => ByAlias.GetValueOrDefault(Normalize(text));

    static string Normalize(string s)
    {
        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        s = s.Replace("°c", "c").Replace("°f", "f").Replace("°", "deg")
             .Replace("²", "2").Replace("³", "3").Replace("^", "")
             .Replace("square ", "sq").Replace("sq ", "sq").Replace("sq.", "sq")
             .Replace("cubic ", "cu").Replace(" per ", "/")
             .Replace("degrees ", "").Replace("degree ", "")
             .Replace("µ", "u").Replace(" ", "");
        return s;
    }

    static void Add(string category, double factor, string symbol, string name, params string[] aliases) =>
        AddOffset(category, factor, 0, symbol, name, aliases);

    static void AddOffset(string category, double factor, double offset, string symbol, string name, params string[] aliases)
    {
        var u = new Unit(category, symbol, name, factor, offset);
        foreach (var a in aliases.Append(symbol).Append(name))
            ByAlias.TryAdd(Normalize(a), u);
    }

    static Units()
    {
        // length (metres)
        Add("length", 1e-9, "nm", "nanometers", "nanometer", "nanometre", "nanometres");
        Add("length", 1e-6, "µm", "micrometers", "um", "micron", "microns", "micrometer", "micrometre");
        Add("length", 0.001, "mm", "millimeters", "millimeter", "millimetre", "millimetres");
        Add("length", 0.01, "cm", "centimeters", "centimeter", "centimetre", "centimetres");
        Add("length", 1, "m", "meters", "meter", "metre", "metres");
        Add("length", 1000, "km", "kilometers", "kilometer", "kilometre", "kilometres");
        Add("length", 0.0254, "in", "inches", "inch", "\"");
        Add("length", 0.3048, "ft", "feet", "foot", "'");
        Add("length", 0.9144, "yd", "yards", "yard", "yds");
        Add("length", 1609.344, "mi", "miles", "mile");
        Add("length", 1852, "nmi", "nautical miles", "nautical mile");

        // mass (kilograms)
        Add("mass", 1e-6, "mg", "milligrams", "milligram");
        Add("mass", 0.001, "g", "grams", "gram");
        Add("mass", 1, "kg", "kilograms", "kilogram", "kilo", "kilos", "kgs");
        Add("mass", 1000, "t", "tonnes", "tonne", "ton", "tons", "metric ton", "metric tons");
        Add("mass", 0.028349523125, "oz", "ounces", "ounce");
        Add("mass", 0.45359237, "lb", "pounds", "pound", "lbs");
        Add("mass", 6.35029318, "st", "stone", "stones");

        // volume (litres)
        Add("volume", 0.001, "ml", "milliliters", "milliliter", "millilitre", "millilitres", "cc", "cm3");
        Add("volume", 0.01, "cl", "centiliters", "centiliter", "centilitre", "centilitres");
        Add("volume", 1, "l", "liters", "liter", "litre", "litres");
        Add("volume", 1000, "m³", "cubic meters", "cubic meter", "cubic metre", "cubic metres", "cum");
        Add("volume", 3.785411784, "gal", "US gallons", "gallon", "gallons");
        Add("volume", 0.946352946, "qt", "US quarts", "quart", "quarts");
        Add("volume", 0.473176473, "pt", "US pints", "pint", "pints");
        Add("volume", 0.2365882365, "cup", "US cups", "cups");
        Add("volume", 0.0295735295625, "fl oz", "US fluid ounces", "floz", "fluid ounce", "fluid ounces");
        Add("volume", 0.01478676478125, "tbsp", "tablespoons", "tablespoon");
        Add("volume", 0.00492892159375, "tsp", "teaspoons", "teaspoon");

        // area (square metres)
        Add("area", 1, "m²", "square meters", "sqm", "sqmeter", "sqmeters", "sqmetre", "sqmetres");
        Add("area", 1e6, "km²", "square kilometers", "sqkm", "sqkilometer", "sqkilometers", "sqkilometre", "sqkilometres");
        Add("area", 0.09290304, "ft²", "square feet", "sqft", "sqfeet", "sqfoot");
        Add("area", 0.00064516, "in²", "square inches", "sqin", "sqinch", "sqinches");
        Add("area", 0.83612736, "yd²", "square yards", "sqyd", "sqyard", "sqyards");
        Add("area", 2589988.110336, "mi²", "square miles", "sqmi", "sqmile", "sqmiles");
        Add("area", 4046.8564224, "acres", "acres", "acre", "ac");
        Add("area", 10000, "ha", "hectares", "hectare");
        Add("area", 25.29285264, "perches", "perches", "perch");

        // speed (metres per second)
        Add("speed", 1, "m/s", "meters per second", "mps", "meters/second", "metres/second");
        Add("speed", 1 / 3.6, "km/h", "kilometers per hour", "kmh", "kph", "km/hr", "km/hour", "kilometers/hour", "kilometres/hour");
        Add("speed", 0.44704, "mph", "miles per hour", "mi/h", "miles/hour");
        Add("speed", 1852.0 / 3600, "kn", "knots", "knot", "kt", "kts");
        Add("speed", 0.3048, "ft/s", "feet per second", "fps", "feet/second");

        // time (seconds)
        Add("time", 0.001, "ms", "milliseconds", "millisecond");
        Add("time", 1, "s", "seconds", "second", "sec", "secs");
        Add("time", 60, "min", "minutes", "minute", "mins");
        Add("time", 3600, "h", "hours", "hour", "hr", "hrs");
        Add("time", 86400, "days", "days", "day", "d");
        Add("time", 604800, "weeks", "weeks", "week", "wk", "wks");
        Add("time", 2629746, "months", "months", "month");
        Add("time", 31556952, "years", "years", "year", "yr", "yrs");

        // data (bytes, binary multiples as Windows reports them)
        Add("data", 0.125, "bits", "bits", "bit");
        Add("data", 1, "B", "bytes", "byte");
        Add("data", 1024, "KB", "kilobytes", "kilobyte", "kib");
        Add("data", 1024d * 1024, "MB", "megabytes", "megabyte", "mib");
        Add("data", 1024d * 1024 * 1024, "GB", "gigabytes", "gigabyte", "gib");
        Add("data", 1024d * 1024 * 1024 * 1024, "TB", "terabytes", "terabyte", "tib");
        Add("data", 1024d * 1024 * 1024 * 1024 * 1024, "PB", "petabytes", "petabyte", "pib");

        // temperature (kelvin): base = (value + offset) * factor
        AddOffset("temperature", 1, 273.15, "°C", "Celsius", "c", "celcius", "centigrade", "degc");
        AddOffset("temperature", 5.0 / 9, 459.67, "°F", "Fahrenheit", "f", "farenheit", "degf");
        AddOffset("temperature", 1, 0, "K", "Kelvin", "kelvins");

        // energy (joules)
        Add("energy", 1, "J", "joules", "joule");
        Add("energy", 1000, "kJ", "kilojoules", "kilojoule");
        Add("energy", 4.184, "cal", "calories", "calorie");
        Add("energy", 4184, "kcal", "kilocalories", "kilocalorie");
        Add("energy", 3600, "Wh", "watt-hours", "watt hour", "watt hours");
        Add("energy", 3.6e6, "kWh", "kilowatt-hours", "kilowatt hour", "kilowatt hours");
        Add("energy", 1055.05585262, "BTU", "BTU", "btus");

        // pressure (pascals)
        Add("pressure", 1, "Pa", "pascals", "pascal");
        Add("pressure", 1000, "kPa", "kilopascals", "kilopascal");
        Add("pressure", 1e5, "bar", "bar", "bars");
        Add("pressure", 6894.757293168, "psi", "psi");
        Add("pressure", 101325, "atm", "atmospheres", "atmosphere");
        Add("pressure", 133.322387415, "mmHg", "mmHg", "torr");

        // power (watts)
        Add("power", 1, "W", "watts", "watt");
        Add("power", 1000, "kW", "kilowatts", "kilowatt");
        Add("power", 745.69987158, "hp", "horsepower");

        // angle (radians)
        Add("angle", 1, "rad", "radians", "radian");
        Add("angle", Math.PI / 180, "deg", "degrees", "degree");

        // what a bare "12 kg" converts to
        foreach (var (a, b) in new[]
        {
            ("km", "mi"), ("mi", "km"), ("m", "ft"), ("ft", "m"), ("cm", "in"), ("in", "cm"), ("mm", "in"),
            ("yd", "m"), ("nmi", "km"), ("kg", "lb"), ("lb", "kg"), ("g", "oz"), ("oz", "g"), ("st", "kg"),
            ("t", "kg"), ("°C", "°F"), ("°F", "°C"), ("K", "°C"), ("l", "gal"), ("gal", "l"), ("ml", "fl oz"),
            ("fl oz", "ml"), ("cup", "ml"), ("tbsp", "ml"), ("tsp", "ml"), ("pt", "ml"), ("qt", "l"),
            ("km/h", "mph"), ("mph", "km/h"), ("m/s", "km/h"), ("kn", "km/h"), ("ft/s", "m/s"),
            ("m²", "ft²"), ("ft²", "m²"), ("km²", "mi²"), ("mi²", "km²"), ("acres", "ha"), ("ha", "acres"),
            ("perches", "m²"), ("KB", "MB"), ("MB", "GB"), ("GB", "MB"), ("TB", "GB"), ("PB", "TB"),
            ("h", "min"), ("min", "s"), ("days", "h"), ("weeks", "days"), ("months", "days"), ("years", "days"),
            ("kcal", "kJ"), ("kJ", "kcal"), ("cal", "J"), ("kWh", "kJ"), ("BTU", "kJ"),
            ("psi", "bar"), ("bar", "psi"), ("atm", "kPa"), ("kPa", "psi"), ("mmHg", "kPa"),
            ("hp", "kW"), ("kW", "hp"), ("deg", "rad"), ("rad", "deg"),
        })
            Defaults[a] = b;
    }
}
