namespace PatchSync.CLI.Commands;

/// <summary>
/// Simple AOT-compatible argument parser.
/// </summary>
public sealed class ArgParser
{
    private readonly string[] _args;
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();

    public ArgParser(string[] args)
    {
        _args = args;
        Parse();
    }

    private void Parse()
    {
        for (int i = 0; i < _args.Length; i++)
        {
            var arg = _args[i];

            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                var eqIndex = key.IndexOf('=');

                if (eqIndex >= 0)
                {
                    // --key=value format
                    _options[key[..eqIndex]] = key[(eqIndex + 1)..];
                }
                else if (i + 1 < _args.Length && !_args[i + 1].StartsWith("-"))
                {
                    // --key value format
                    _options[key] = _args[++i];
                }
                else
                {
                    // --flag (boolean)
                    _options[key] = "true";
                }
            }
            else if (arg.StartsWith("-") && arg.Length == 2)
            {
                var key = arg[1..];
                if (i + 1 < _args.Length && !_args[i + 1].StartsWith("-"))
                {
                    _options[key] = _args[++i];
                }
                else
                {
                    _options[key] = "true";
                }
            }
            else
            {
                _positional.Add(arg);
            }
        }
    }

    public bool HasHelp => Has("help") || Has("h") || Has("?");

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name, string? shortName = null)
    {
        if (_options.TryGetValue(name, out var value))
            return value;
        if (shortName != null && _options.TryGetValue(shortName, out value))
            return value;
        return null;
    }

    public string GetRequired(string name, string? shortName = null)
    {
        var value = Get(name, shortName);
        if (value == null)
            throw new ArgumentException($"Missing required option: --{name}");
        return value;
    }

    public string GetOrDefault(string name, string defaultValue, string? shortName = null)
    {
        return Get(name, shortName) ?? defaultValue;
    }

    public int GetInt(string name, int defaultValue, string? shortName = null)
    {
        var value = Get(name, shortName);
        return value != null && int.TryParse(value, out var result) ? result : defaultValue;
    }

    public long GetLong(string name, long defaultValue, string? shortName = null)
    {
        var value = Get(name, shortName);
        return value != null && long.TryParse(value, out var result) ? result : defaultValue;
    }

    public double GetDouble(string name, double defaultValue, string? shortName = null)
    {
        var value = Get(name, shortName);
        return value != null && double.TryParse(value, out var result) ? result : defaultValue;
    }

    public bool GetBool(string name, bool defaultValue = false, string? shortName = null)
    {
        var value = Get(name, shortName);
        if (value == null) return defaultValue;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.Ordinal) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Positional => _positional;

    public string? FirstPositional => _positional.Count > 0 ? _positional[0] : null;
}
