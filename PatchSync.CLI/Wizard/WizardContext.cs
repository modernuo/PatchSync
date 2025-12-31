namespace PatchSync.CLI.Wizard;

/// <summary>
/// Holds state for a wizard session, including all collected values.
/// Uses a dictionary for AOT-compatible storage.
/// </summary>
public sealed class WizardContext
{
    private readonly Dictionary<string, object?> _values = new();

    /// <summary>Current step index (0-based).</summary>
    public int CurrentStep { get; set; }

    /// <summary>Total number of steps in the wizard.</summary>
    public int TotalSteps { get; set; }

    /// <summary>Whether this is the first step (no back available).</summary>
    public bool IsFirstStep => CurrentStep == 0;

    /// <summary>Whether this is the last step.</summary>
    public bool IsLastStep => CurrentStep >= TotalSteps - 1;

    /// <summary>Set a value by key.</summary>
    public void Set<T>(string key, T value)
    {
        _values[key] = value;
    }

    /// <summary>Get a value by key, throwing if not found.</summary>
    public T Get<T>(string key)
    {
        if (_values.TryGetValue(key, out var value) && value is T typed)
            return typed;
        throw new InvalidOperationException($"Wizard value '{key}' not found or wrong type");
    }

    /// <summary>Try to get a value by key.</summary>
    public bool TryGet<T>(string key, out T? value)
    {
        if (_values.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Get a value or return default if not found.</summary>
    public T? GetOrDefault<T>(string key, T? defaultValue = default)
    {
        if (TryGet<T>(key, out var value))
            return value;
        return defaultValue;
    }

    /// <summary>Check if a value exists.</summary>
    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary>Remove a value (used when going back).</summary>
    public void Remove(string key) => _values.Remove(key);

    /// <summary>Clear all values.</summary>
    public void Clear() => _values.Clear();

    /// <summary>Get all keys.</summary>
    public IEnumerable<string> Keys => _values.Keys;
}
