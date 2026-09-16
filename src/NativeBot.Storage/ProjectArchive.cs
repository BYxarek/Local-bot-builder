using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NativeBot.Core;

namespace NativeBot.Storage;

public sealed record ProjectManifest(int FormatVersion, DateTimeOffset ExportedAt, string BotName, string BotUsername, BotUpdateMode UpdateMode);
public sealed record BotProjectFile(
    ProjectManifest Manifest,
    IReadOnlyList<FlowDefinition> Flows,
    IReadOnlyList<FlowVersion> Versions,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<Asset> AssetLinks);

public sealed partial class AppDatabase
{
    private static readonly JsonSerializerOptions ProjectJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportProjectAsync(Guid botId, string destinationPath, CancellationToken cancellationToken = default)
    {
        var bot = (await GetBotsAsync(cancellationToken)).Single(x => x.Id == botId);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (await GetSettingAsync($"PrivacyModeDisabled:{botId}", cancellationToken) is { } privacy)
            settings["PrivacyModeDisabled"] = privacy;
        var project = new BotProjectFile(
            new(1, DateTimeOffset.UtcNow, bot.Name, bot.Username, bot.UpdateMode),
            await GetDraftsAsync(botId, cancellationToken),
            await GetAllFlowVersionsAsync(botId, cancellationToken),
            settings,
            await GetAssetsAsync(botId, cancellationToken));
        await File.WriteAllTextAsync(destinationPath, JsonSerializer.Serialize(project, ProjectJson), cancellationToken);
    }

    public async Task ImportProjectAsync(Guid botId, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(sourcePath).Length > 50 * 1024 * 1024) throw new InvalidDataException("Файл проекта превышает 50 МБ.");
        var project = JsonSerializer.Deserialize<BotProjectFile>(await File.ReadAllTextAsync(sourcePath, cancellationToken), ProjectJson)
            ?? throw new InvalidDataException("Файл проекта повреждён.");
        if (project.Manifest.FormatVersion != 1) throw new InvalidDataException("Версия файла проекта не поддерживается.");

        var ids = project.Flows.Select(x => x.Id).Concat(project.Versions.Select(x => x.FlowId)).Distinct().ToDictionary(x => x, _ => Guid.NewGuid());
        FlowDefinition Remap(FlowDefinition flow) => flow with { Id = ids[flow.Id] };
        foreach (var flow in project.Flows) await SaveDraftAsync(botId, Remap(flow), cancellationToken);
        foreach (var version in project.Versions)
        {
            var definition = Remap(version.Definition);
            if (!project.Flows.Any(x => x.Id == version.FlowId)) await SaveDraftAsync(botId, definition, cancellationToken);
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "INSERT INTO FlowVersions(Id,FlowId,Version,Status,CreatedAt,PublishedAt,DefinitionJson) VALUES($id,$flowId,$version,$status,$createdAt,$publishedAt,$json);", cancellationToken,
                ("$id", Guid.NewGuid()), ("$flowId", ids[version.FlowId]), ("$version", version.Version), ("$status", (int)version.Status),
                ("$createdAt", version.CreatedAt), ("$publishedAt", version.PublishedAt), ("$json", JsonSerializer.Serialize(definition)));
        }
        foreach (var asset in project.AssetLinks)
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "INSERT OR IGNORE INTO Assets(Id,BotId,Name,ContentType,Size,Hash,Path) VALUES($id,$botId,$name,$type,$size,$hash,$path);", cancellationToken,
                ("$id", Guid.NewGuid()), ("$botId", botId), ("$name", asset.Name), ("$type", asset.ContentType), ("$size", asset.Size), ("$hash", asset.Hash), ("$path", asset.Path));
        }
        if (project.Settings.TryGetValue("PrivacyModeDisabled", out var privacy))
            await SetSettingAsync($"PrivacyModeDisabled:{botId}", privacy, cancellationToken);
        await SetBotUpdateModeAsync(botId, project.Manifest.UpdateMode, cancellationToken);
    }

    private async Task<IReadOnlyList<FlowVersion>> GetAllFlowVersionsAsync(Guid botId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT v.Id,v.FlowId,v.Version,v.Status,v.CreatedAt,v.PublishedAt,v.DefinitionJson FROM FlowVersions v JOIN Flows f ON f.Id=v.FlowId WHERE f.BotId=$botId ORDER BY v.FlowId,v.Version;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FlowVersion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(6)) ?? throw new InvalidDataException("Не удалось прочитать сценарий.");
            result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2), (FlowVersionStatus)reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), definition));
        }
        return result;
    }

    private async Task<IReadOnlyList<Asset>> GetAssetsAsync(Guid botId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,ContentType,Size,Hash,Path FROM Assets WHERE BotId=$botId ORDER BY Name;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Asset>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(Guid.Parse(reader.GetString(0)), botId, reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }
}
