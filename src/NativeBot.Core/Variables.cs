using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NativeBot.Core;

public sealed class VariableContext
{
    private static readonly Regex Placeholder = new(@"\{(?<name>[a-zA-Z][a-zA-Z0-9_.]*)\}", RegexOptions.Compiled);
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, VariableDefinition> _definitions;

    public VariableContext(IEnumerable<VariableDefinition>? definitions = null)
    {
        _definitions = (definitions ?? []).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        _values["system.now"] = DateTimeOffset.Now;
        _values["system.date"] = DateOnly.FromDateTime(DateTime.Today);
    }

    public object? this[string name]
    {
        get => _values.TryGetValue(name, out var value) ? value : null;
        set => Set(name, value);
    }

    public void Set(string name, object? value)
    {
        if (name.StartsWith("system.", StringComparison.OrdinalIgnoreCase) ||
            (_definitions.TryGetValue(name, out var definition) && definition.IsReadOnly))
            throw new InvalidOperationException($"Переменная {name} доступна только для чтения.");

        if (definition is not null && !MatchesType(value, definition.Type))
            throw new ArgumentException($"Значение не соответствует типу {definition.Type} переменной {name}.", nameof(value));

        _values[name] = value;
    }

    public void SetRuntime(string name, object? value)
    {
        if (!name.StartsWith("system.", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Только системные переменные и переменные события устанавливаются средой выполнения.");
        if (_definitions.TryGetValue(name, out var definition) && !MatchesType(value, definition.Type))
            throw new ArgumentException($"Значение не соответствует типу {definition.Type} переменной {name}.", nameof(value));
        _values[name] = value;
    }

    public string Interpolate(string template) => Placeholder.Replace(template, match =>
    {
        var name = match.Groups["name"].Value;
        return _values.TryGetValue(name, out var value) ? Format(value) : match.Value;
    });

    public IReadOnlyCollection<string> Names => _values.Keys.Concat(_definitions.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTimeOffset date => date.ToString("g", CultureInfo.CurrentCulture),
        DateOnly date => date.ToString("d", CultureInfo.CurrentCulture),
        string text => text,
        _ => JsonSerializer.Serialize(value)
    };

    private static bool MatchesType(object? value, VariableType type) => value is null || type switch
    {
        VariableType.Text => value is string,
        VariableType.Number => value is byte or short or int or long or float or double or decimal,
        VariableType.Boolean => value is bool,
        VariableType.DateTime => value is DateTime or DateTimeOffset or DateOnly,
        VariableType.List => value is System.Collections.IEnumerable and not string,
        VariableType.Object => value is JsonElement or JsonDocument or IReadOnlyDictionary<string, object?>,
        _ => false
    };
}
