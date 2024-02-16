namespace PatchSync.CLI;

public class CLIContext
{
    private readonly Dictionary<string, object> _context = new();

    public bool TryGetProperty<T>(string key, out T? t)
    {
        if (_context.TryGetValue(key, out var value) && value is T tValue)
        {
            t = tValue;
            return true;
        }

        t = default;
        return false;
    }

    public void SetProperty<T>(string key, T? value) =>
        _context[key] = value!;
}
