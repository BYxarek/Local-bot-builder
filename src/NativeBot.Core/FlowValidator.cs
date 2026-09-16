using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NativeBot.Core;

public sealed record ValidationIssue(Guid? NodeId, string Code, string Message, bool IsError = true);

public sealed class FlowValidator
{
    private static readonly Regex VariableReference = new(@"\{(?<name>[a-zA-Z][a-zA-Z0-9_.]*)\}", RegexOptions.Compiled);
    private static readonly string[] BuiltInPrefixes = ["system.", "event."];

    public IReadOnlyList<ValidationIssue> Validate(
        FlowDefinition flow,
        ISet<Guid>? knownFlows = null,
        ISet<Guid>? knownAssets = null)
    {
        var issues = new List<ValidationIssue>();
        var nodes = flow.Nodes.ToDictionary(x => x.Id);
        var variables = (flow.Variables ?? []).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        if (!nodes.ContainsKey(flow.EntryNodeId))
            issues.Add(new(null, "entry.missing", "Начальный блок не найден."));

        foreach (var node in flow.Nodes)
        {
            foreach (var target in node.Next?.Values ?? [])
                if (!nodes.ContainsKey(target))
                    issues.Add(new(node.Id, "transition.missing", "Переход ведёт на отсутствующий блок."));

            CheckParameters(node, variables, knownFlows, knownAssets, issues);
        }

        CheckReachability(flow, nodes, issues);
        CheckCycles(flow, nodes, issues);
        return issues;
    }

    private static void CheckParameters(
        FlowNode node,
        IReadOnlyDictionary<string, VariableDefinition> variables,
        ISet<Guid>? knownFlows,
        ISet<Guid>? knownAssets,
        List<ValidationIssue> issues)
    {
        var parameters = node.Parameters;
        if (parameters is null) return;

        foreach (var value in parameters.SelectMany(LeafStrings))
        foreach (Match match in VariableReference.Matches(value))
        {
            var name = match.Groups["name"].Value;
            if (!BuiltInPrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)) && !variables.ContainsKey(name))
                issues.Add(new(node.Id, "variable.unknown", $"Неизвестная переменная: {name}."));
        }

        if (ReadGuid(parameters, "flowId") is { } flowId && knownFlows is not null && !knownFlows.Contains(flowId))
            issues.Add(new(node.Id, "flow.missing", "Указанный сценарий не найден."));
        if (ReadGuid(parameters, "assetId") is { } assetId && knownAssets is not null && !knownAssets.Contains(assetId))
            issues.Add(new(node.Id, "asset.missing", "Вложение не найдено."));
        if (node.Handler == "wait" && !PositiveInt(parameters, "timeoutSeconds"))
            issues.Add(new(node.Id, "wait.invalid", "Время ожидания должно быть положительным."));
        if (node.Handler == "payment" && string.IsNullOrWhiteSpace(parameters["currency"]?.GetValue<string>()))
            issues.Add(new(node.Id, "payment.invalid", "Для платежа требуется валюта."));
        if (parameters["requiresAdmin"]?.GetValue<bool>() == true && parameters["hasPermission"]?.GetValue<bool>() == false)
            issues.Add(new(node.Id, "permission.missing", "У бота нет необходимых прав."));
        if (parameters["requiredRights"] is JsonArray rights && parameters["grantedRights"] is JsonArray granted)
        {
            var available = granted.Select(x => x?.GetValue<string>()).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var right in rights.Select(x => x?.GetValue<string>()).Where(x => x is not null && !available.Contains(x)))
                issues.Add(new(node.Id, "permission.missing", $"У бота нет права: {right}."));
        }
        if (parameters["requiresGroupMessages"]?.GetValue<bool>() == true && parameters["privacyModeDisabled"]?.GetValue<bool>() != true)
            issues.Add(new(node.Id, "privacy_mode", "Отключите Privacy Mode в BotFather, чтобы бот получал обычные сообщения группы."));
        if (parameters["allowedChat"]?.GetValue<string>() is { } allowed && parameters["chatType"]?.GetValue<string>() is { } actual && allowed != actual)
            issues.Add(new(node.Id, "chat.incompatible", "Действие недоступно для этого типа чата."));
        if (parameters["buttonType"]?.GetValue<string>() == "url" && !Uri.TryCreate(parameters["url"]?.GetValue<string>(), UriKind.Absolute, out _))
            issues.Add(new(node.Id, "button.invalid", "Для URL-кнопки требуется корректная ссылка."));
        if (parameters["variable"]?.GetValue<string>() is { } variable && variables.TryGetValue(variable, out var definition) &&
            parameters["valueType"]?.GetValue<string>() is { } valueType && !definition.Type.ToString().Equals(valueType, StringComparison.OrdinalIgnoreCase))
            issues.Add(new(node.Id, "variable.type", "Тип значения не совпадает с типом переменной."));
    }

    private static void CheckReachability(FlowDefinition flow, IReadOnlyDictionary<Guid, FlowNode> nodes, List<ValidationIssue> issues)
    {
        if (!nodes.ContainsKey(flow.EntryNodeId)) return;
        var reached = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(flow.EntryNodeId);
        while (queue.TryDequeue(out var id) && reached.Add(id))
            foreach (var target in nodes[id].Next?.Values ?? [])
                if (nodes.ContainsKey(target)) queue.Enqueue(target);

        foreach (var node in flow.Nodes.Where(x => !reached.Contains(x.Id)))
            issues.Add(new(node.Id, "node.unreachable", "Блок недостижим.", false));
    }

    private static void CheckCycles(FlowDefinition flow, IReadOnlyDictionary<Guid, FlowNode> nodes, List<ValidationIssue> issues)
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (!nodes.ContainsKey(id) || visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            var cycle = (nodes[id].Next?.Values ?? []).Any(Visit);
            visiting.Remove(id);
            visited.Add(id);
            return cycle;
        }

        if (Visit(flow.EntryNodeId))
            issues.Add(new(null, "flow.cycle", "Обнаружен потенциально бесконечный цикл.", false));
    }

    private static IEnumerable<string> LeafStrings(KeyValuePair<string, JsonNode?> pair) => pair.Value switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        JsonObject obj => obj.SelectMany(LeafStrings),
        JsonArray array => array.SelectMany((value, index) => LeafStrings(new(index.ToString(), value))),
        _ => []
    };

    private static Guid? ReadGuid(JsonObject value, string name) => Guid.TryParse(value[name]?.GetValue<string>(), out var id) ? id : null;
    private static bool PositiveInt(JsonObject value, string name) => value[name]?.GetValue<int>() is > 0;
}
