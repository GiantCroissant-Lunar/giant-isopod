using System.Text.Json;
using System.Text.Json.Serialization;
using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Actors;

public interface ITaskGraphCheckpointStore
{
    IReadOnlyList<TaskGraphCheckpoint> LoadAll();
    void Save(TaskGraphCheckpoint checkpoint);
    void Delete(string graphId);
}

public sealed class NullTaskGraphCheckpointStore : ITaskGraphCheckpointStore
{
    public static readonly NullTaskGraphCheckpointStore Instance = new();

    public IReadOnlyList<TaskGraphCheckpoint> LoadAll() => Array.Empty<TaskGraphCheckpoint>();
    public void Save(TaskGraphCheckpoint checkpoint) { }
    public void Delete(string graphId) { }
}

public sealed class FileTaskGraphCheckpointStore : ITaskGraphCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    static FileTaskGraphCheckpointStore()
    {
        JsonOptions.Converters.Add(new ReadOnlySetJsonConverterFactory());
    }

    private readonly string _basePath;

    public FileTaskGraphCheckpointStore(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(_basePath);
    }

    public IReadOnlyList<TaskGraphCheckpoint> LoadAll()
    {
        if (!Directory.Exists(_basePath))
            return Array.Empty<TaskGraphCheckpoint>();

        var checkpoints = new List<TaskGraphCheckpoint>();
        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var checkpoint = JsonSerializer.Deserialize<TaskGraphCheckpoint>(json, JsonOptions);
                if (checkpoint is not null)
                    checkpoints.Add(checkpoint);
            }
            catch
            {
            }
        }

        return checkpoints;
    }

    public void Save(TaskGraphCheckpoint checkpoint)
    {
        var path = GetPath(checkpoint.GraphId);
        var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void Delete(string graphId)
    {
        var path = GetPath(graphId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string GetPath(string graphId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(graphId.Select(ch => invalid.Contains(ch) ? '_' : ch));
        return Path.Combine(_basePath, $"{safe}.json");
    }
}

public sealed record TaskGraphCheckpoint(
    string GraphId,
    TaskBudget? GraphBudget,
    IReadOnlyList<TaskNode> Nodes,
    IReadOnlyList<TaskEdge> Edges,
    IReadOnlyDictionary<string, TaskNodeStatus> Status,
    IReadOnlyDictionary<string, int> Depth,
    IReadOnlyDictionary<string, string?> ParentTaskId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ChildTaskIds,
    IReadOnlyDictionary<string, StopCondition?> StopConditions,
    IReadOnlyDictionary<string, string> AssignedAgent,
    IReadOnlyDictionary<string, TaskCompleted> CompletedResults,
    IReadOnlyDictionary<string, TaskCompleted> PendingMerge,
    IReadOnlyDictionary<string, PendingValidationCheckpoint> PendingValidation,
    IReadOnlyDictionary<string, int> ValidationAttempts,
    IReadOnlyList<string> PendingWorkspaceRelease);

public sealed record PendingValidationCheckpoint(
    TaskCompleted Completed,
    int RemainingArtifacts,
    IReadOnlyList<ValidatorResult> AllResults);

internal sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsGenericType &&
               typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var items = JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? new List<T>();
            return new HashSet<T>(items);
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<T> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value.ToArray(), options);
        }
    }
}
