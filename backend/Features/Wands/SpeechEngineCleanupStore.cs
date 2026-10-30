using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Wands;

// Engine resources outlive a process. Persist only their IDs and expiry so a
// failed DELETE or an app restart cannot accumulate abandoned remote resources.
internal sealed class SpeechEngineCleanupStore(IConfiguration configuration)
{
    private readonly string? path = configuration["ApplicationIdentity:DataRoot"] is { Length: > 0 } root
        ? Path.Combine(Path.GetFullPath(root), "speech-engine-cleanup.json") : null;
    private readonly object gate = new();
    internal sealed record Resource(string Id, DateTimeOffset Deadline);

    internal Resource[] Read() => Locked(values => values.ToArray(), false);
    internal void Record(string id, DateTimeOffset deadline) => Locked(values =>
    {
        if (!ValidId(id) || values.Count >= 500) throw new InvalidDataException("Invalid Speech Engine cleanup state.");
        values.RemoveAll(value => value.Id == id);
        values.Add(new(id, deadline));
        return true;
    }, true);
    internal void Forget(string id) => Locked(values => { values.RemoveAll(value => value.Id == id); return true; }, true);

    private T Locked<T>(Func<List<Resource>, T> action, bool write)
    {
        if (path is null) throw new InvalidOperationException("Speech Engine requires durable account data storage.");
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var lease = Lease(path + ".lock");
            if (File.Exists(path) && new FileInfo(path).Length > 131072) throw new InvalidDataException("Invalid Speech Engine cleanup state.");
            var values = File.Exists(path)
                ? JsonSerializer.Deserialize<List<Resource>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid Speech Engine cleanup state.")
                : [];
            if (values.Count > 500 || values.Any(value => !ValidId(value.Id))) throw new InvalidDataException("Invalid Speech Engine cleanup state.");
            var result = action(values);
            if (!write) return result;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, values);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
    }

    private static bool ValidId(string? id) => id is { Length: > 5 and < 100 }
        && Regex.IsMatch(id, "\\Aseng_[a-zA-Z0-9_]+\\z");

    private static FileStream Lease(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }
}
