using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Push;

internal sealed class PushStore
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal PushStore(string dataRoot, IDataProtectionProvider protection)
    {
        Directory.CreateDirectory(dataRoot);
        _protector = protection.CreateProtector("DildoVander.Push.Subscriptions.v1");
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(dataRoot, "push.sqlite"), ForeignKeys = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS push_subscriptions (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL, session_id TEXT NOT NULL,
                platform TEXT NOT NULL, registration TEXT NOT NULL, snapshot TEXT NOT NULL,
                UNIQUE(session_id,platform));
            CREATE TABLE IF NOT EXISTS push_outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT, subscription_id TEXT NOT NULL,
                event_key TEXT NOT NULL, message TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                next_at INTEGER NOT NULL, expires_at INTEGER NOT NULL,
                FOREIGN KEY(subscription_id) REFERENCES push_subscriptions(id) ON DELETE CASCADE,
                UNIQUE(subscription_id,event_key));
            """;
        command.ExecuteNonQuery();
    }

    internal bool Register(string userId, string sessionId, PushRegistration registration,
        IReadOnlyDictionary<string, PushMessage> baseline)
    {
        var normalized = registration.Platform == "web"
            ? registration with { Token = null, Endpoint = new Uri(registration.Endpoint!).AbsoluteUri }
            : new PushRegistration(registration.Platform, registration.Platform == "ios"
                ? registration.Token!.ToLowerInvariant() : registration.Token);
        var id = RegistrationId(normalized);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var owner = connection.CreateCommand();
        owner.Transaction = transaction;
        owner.CommandText = "SELECT user_id,session_id FROM push_subscriptions WHERE id=$id";
        owner.Parameters.AddWithValue("$id", id);
        using (var reader = owner.ExecuteReader())
        {
            if (reader.Read() && (reader.GetString(0) != userId || reader.GetString(1) != sessionId))
            { return false; }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM push_subscriptions WHERE session_id=$session AND platform=$platform AND id!=$id;
            INSERT INTO push_subscriptions(id,user_id,session_id,platform,registration,snapshot)
                VALUES($id,$user,$session,$platform,$registration,$snapshot)
                ON CONFLICT(id) DO UPDATE SET registration=excluded.registration;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$platform", normalized.Platform);
        command.Parameters.AddWithValue("$registration", Protect(normalized));
        command.Parameters.AddWithValue("$snapshot", Protect(baseline.Keys.ToArray()));
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    private static string RegistrationId(PushRegistration registration)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(registration.Platform + ":"
            + (registration.Platform switch
            {
                "web" => new Uri(registration.Endpoint!).AbsoluteUri,
                "ios" => registration.Token!.ToLowerInvariant(),
                _ => registration.Token
            }))));

    internal PushSubscription? FindRegistration(PushRegistration registration)
        => Subscriptions(id: RegistrationId(registration)).SingleOrDefault();

    internal bool IsCurrent(PushSubscription subscription)
        => Subscriptions(id: subscription.Id).Any(current => current.UserId == subscription.UserId
            && current.SessionId == subscription.SessionId);

    internal IReadOnlyList<PushSubscription> Subscriptions(string? sessionId = null, string? id = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,user_id,session_id,registration FROM push_subscriptions WHERE ($session IS NULL OR session_id=$session) AND ($id IS NULL OR id=$id)";
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<PushSubscription>();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Unprotect<PushRegistration>(reader.GetString(3))));
        }
        return result;
    }

    internal void Remove(PushSubscription subscription)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM push_subscriptions WHERE id=$id AND user_id=$user AND session_id=$session";
        command.Parameters.AddWithValue("$id", subscription.Id);
        command.Parameters.AddWithValue("$user", subscription.UserId);
        command.Parameters.AddWithValue("$session", subscription.SessionId);
        command.ExecuteNonQuery();
    }
    internal void RemoveAccount(string userId) => Delete("user_id", userId);
    internal void RemoveSession(string sessionId) => Delete("session_id", sessionId);
    private void Delete(string column, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM push_subscriptions WHERE {column}=$value";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal void Observe(PushSubscription subscription, IReadOnlyDictionary<string, PushMessage> events, long now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT snapshot FROM push_subscriptions WHERE id=$id AND user_id=$user AND session_id=$session";
        read.Parameters.AddWithValue("$id", subscription.Id);
        read.Parameters.AddWithValue("$user", subscription.UserId);
        read.Parameters.AddWithValue("$session", subscription.SessionId);
        if (read.ExecuteScalar() is not string snapshot) { return; }
        var previous = Unprotect<string[]>(snapshot).ToHashSet(StringComparer.Ordinal);
        foreach (var item in events.Where(item => !previous.Contains(item.Key)))
        { Enqueue(connection, transaction, subscription, item.Key, item.Value, now); }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE push_subscriptions SET snapshot=$snapshot WHERE id=$id AND user_id=$user AND session_id=$session";
        update.Parameters.AddWithValue("$snapshot", Protect(events.Keys.ToArray()));
        update.Parameters.AddWithValue("$id", subscription.Id);
        update.Parameters.AddWithValue("$user", subscription.UserId);
        update.Parameters.AddWithValue("$session", subscription.SessionId);
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    internal void Test(PushSubscription subscription, long now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Enqueue(connection, transaction, subscription, "test:" + Guid.NewGuid().ToString("N"),
            new("Wand Night Stand", "Oznámení jsou připravena. Opona zatím drží.", "/me/account", "test"), now);
        transaction.Commit();
    }

    private void Enqueue(SqliteConnection connection, SqliteTransaction transaction, PushSubscription subscription,
        string key, PushMessage message, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO push_outbox(subscription_id,event_key,message,next_at,expires_at)
                SELECT id,$key,$message,$now,$expires FROM push_subscriptions
                WHERE id=$subscription AND user_id=$user AND session_id=$session;
            """;
        command.Parameters.AddWithValue("$subscription", subscription.Id);
        command.Parameters.AddWithValue("$user", subscription.UserId);
        command.Parameters.AddWithValue("$session", subscription.SessionId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$message", Protect(message));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expires", now + 600);
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<PushDelivery> Due(long now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM push_outbox WHERE expires_at<=$now;
            SELECT o.id,s.id,s.user_id,s.session_id,s.registration,o.event_key,o.message,o.attempts,o.expires_at
                FROM push_outbox o JOIN push_subscriptions s ON s.id=o.subscription_id
                WHERE o.next_at<=$now ORDER BY o.id LIMIT 100;
            """;
        command.Parameters.AddWithValue("$now", now);
        using var reader = command.ExecuteReader();
        var result = new List<PushDelivery>();
        while (reader.Read())
        {
            result.Add(new(reader.GetInt64(0),
                new(reader.GetString(1), reader.GetString(2), reader.GetString(3), Unprotect<PushRegistration>(reader.GetString(4))),
                reader.GetString(5), Unprotect<PushMessage>(reader.GetString(6)), reader.GetInt32(7), reader.GetInt64(8)));
        }
        return result;
    }

    internal void Complete(PushDelivery delivery, PushSendResult result, long now)
    {
        if (result == PushSendResult.InvalidSubscription) { Remove(delivery.Subscription); return; }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = result == PushSendResult.Retry && delivery.Attempts < 4
            ? "UPDATE push_outbox SET attempts=attempts+1,next_at=$next WHERE id=$id"
            : "DELETE FROM push_outbox WHERE id=$id";
        command.Parameters.AddWithValue("$id", delivery.Id);
        command.Parameters.AddWithValue("$next", now + Math.Min(240, 15 * (1 << delivery.Attempts)));
        command.ExecuteNonQuery();
    }

    private string Protect<T>(T value) => _protector.Protect(JsonSerializer.Serialize(value, Json));
    private T Unprotect<T>(string value) => JsonSerializer.Deserialize<T>(_protector.Unprotect(value), Json)!;
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
