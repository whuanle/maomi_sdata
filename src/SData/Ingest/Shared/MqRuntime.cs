using System.Collections.Concurrent;

namespace SData.Ingest;

/// <summary>消息消费运行态快照（由 Monitor 暴露，不含连接凭据）.</summary>
public class MqRuntimeItem
{
    public long InstanceId { get; set; }
    public int RetryAttempt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string State { get; set; } = "starting";
    public long PersistedRecords { get; set; }
    public DateTimeOffset? LastPersistedAt { get; set; }
    public string? LastError { get; set; }
    public IReadOnlyList<MqTopicRuntimeItem> Topics { get; set; } = [];
}

/// <summary>配置主题规则的消费统计，速率使用最近 60 秒窗口.</summary>
public class MqTopicRuntimeItem
{
    public string Stage { get; set; } = "connecting";
    public DateTimeOffset? StageSince { get; set; }
    public int PendingPersistence { get; set; }
    public int PendingConfirmation { get; set; }
    public DateTimeOffset? LastProgressAt { get; set; }
    public IReadOnlyList<MqFailureItem> Failures { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public string MatchMode { get; set; } = "exact";
    public string State { get; set; } = "starting";
    public int MatchedTopics { get; set; }
    public long ReceivedMessages { get; set; }
    public long ReceivedBytes { get; set; }
    public long PersistedRecords { get; set; }
    public long ParseFailures { get; set; }
    public int BlockedMessages { get; set; }
    public DateTimeOffset? BlockedSince { get; set; }
    public long ConfirmedMessages { get; set; }
    public long ConfirmationFailures { get; set; }
    public double MessagesPerSecond { get; set; }
    public double RecordsPerSecond { get; set; }
    public double BytesPerSecond { get; set; }
    public DateTimeOffset? LastPersistedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>最近阶段失败；原因只能使用安全分类，禁止消息原文与连接凭据.</summary>
public class MqFailureItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ActualTopic { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public long Occurrences { get; set; }
}

/// <summary>进程内运行态；按实例加锁，窗口桶固定大小，不保存消息内容.</summary>
public static class MqRuntime
{
    private static readonly ConcurrentDictionary<long, Entry> Items = new();

    public static void Start(long id, IEnumerable<(string Name, string MatchMode)> topics)
    {
        var entry = new Entry();
        foreach (var topic in topics)
        {
            entry.Topics.TryAdd(Key(topic.Name, topic.MatchMode), new TopicEntry(topic.Name, topic.MatchMode));
        }

        Items[id] = entry;
    }

    public static void Remove(long id) => Items.TryRemove(id, out _);

    public static IReadOnlyList<MqRuntimeItem> Snapshot() => Snapshot(DateTimeOffset.UtcNow);

    public static IReadOnlyList<MqRuntimeItem> Snapshot(DateTimeOffset now)
    {
        return Items.OrderBy(x => x.Key).Select(pair =>
        {
            lock (pair.Value)
            {
                var entry = pair.Value;
                var topics = entry.Topics.Values.Select(topic => Copy(topic, entry.State, now)).ToList();
                return new MqRuntimeItem
                {
                    InstanceId = pair.Key, SessionId = entry.SessionId,
                    RetryAttempt = entry.RetryAttempt, NextRetryAt = entry.NextRetryAt,
                    State = entry.NextRetryAt > now ? "retrying" : entry.State == "connected"
                        ? topics.Any(t => t.State == "error") ? "error"
                            : topics.Any(t => t.State == "starting") ? "starting"
                            : topics.Any(t => t.State == "persisting") ? "persisting"
                            : topics.Any(t => t.State == "consuming") ? "consuming" : "idle"
                        : entry.State,
                    PersistedRecords = entry.LegacyPersisted + topics.Sum(t => t.PersistedRecords),
                    LastPersistedAt = topics.Select(t => t.LastPersistedAt).Append(entry.LastPersistedAt).Max(),
                    LastError = entry.Error ?? topics.Select(t => t.LastError).FirstOrDefault(e => e != null),
                    Topics = topics,
                };
            }
        }).ToList();
    }

    public static void SetState(long id, string state, string? error = null)
    {
        var entry = Items.GetOrAdd(id, _ => new Entry());
        lock (entry)
        {
            entry.State = state;
            entry.Error = error;
            if (error != null) MqFailureEvents.Publish(new(id, "", "connecting", "connection_failed", "连接、认证或主题发现失败", DateTimeOffset.UtcNow));
            foreach (var topic in entry.Topics.Values)
            {
                if (error != null) Failure(topic, "connecting", "connection_failed", "连接、认证或主题发现失败，请检查消息源配置");
                else if (state == "connected") Recover(topic, "connecting");
            }
        }
    }

    public static void RetryScheduled(long id, int attempt, DateTimeOffset next)
    {
        if (!Items.TryGetValue(id, out var entry)) return;
        lock (entry) { entry.RetryAttempt = attempt; entry.NextRetryAt = next; }
    }

    public static void RetryFinished(long id)
    {
        if (!Items.TryGetValue(id, out var entry)) return;
        lock (entry) { entry.NextRetryAt = null; }
    }

    public static bool IsHealthy(long id)
    {
        if (!Items.TryGetValue(id, out var entry)) return false;
        lock (entry) return entry.State == "connected" && entry.NextRetryAt == null
            && entry.Topics.Values.All(topic => topic.Blocked.Count == 0 && !topic.PersistenceError && topic.Error == null
                && topic.Failures.Values.All(failure => failure.RecoveredAt != null)
                && topic.States.Values.All(state => state == "connected"));
    }

    public static void Connected(long id) => SetState(id, "connected");

    public static void TopicState(long id, string name, string mode, string actualTopic, string state)
        => Change(id, name, mode, topic =>
        {
            topic.States[actualTopic] = state;
            if (state == "error")
            {
                Failure(topic, "connecting", "connection_failed", "主题连接中断，等待客户端恢复");
                topic.Failures["connecting"].ActualTopic = actualTopic;
            }
            else if (state == "connected" && topic.States.Values.All(value => value == "connected")) Recover(topic, "connecting");
        });

    public static void Matched(long id, string name, string mode, IEnumerable<string> actualTopics)
    {
        Change(id, name, mode, topic =>
        {
            topic.Matched.Clear();
            topic.Matched.UnionWith(actualTopics);
        });
    }

    public static void Received(long id, string name, string mode, string actualTopic, int bytes)
    {
        Change(id, name, mode, topic =>
        {
            topic.Matched.Add(actualTopic);
            topic.Received++;
            topic.Bytes += bytes;
            var bucket = Bucket(topic, DateTimeOffset.UtcNow);
            bucket.Messages++;
            bucket.Bytes += bytes;
        });
    }

    public static string? ParseFailed(long id, string name, string mode, string code, string reason, string actualTopic)
    {
        string? diagnosticId = null;
        Change(id, name, mode, topic =>
        {
            Failure(topic, "parse", code, reason);
            var failure = topic.Failures["parse"];
            failure.ActualTopic = actualTopic;
            diagnosticId = failure.Id;
        });
        return diagnosticId;
    }

    public static void Decoded(long id, string name, string mode, bool failed)
        => Change(id, name, mode, topic =>
        {
            if (failed) topic.ParseFailures++;
            else Recover(topic, "parse");
        });

    public static Guid BeginPersistence(long id, string name, string mode)
    {
        var token = Guid.NewGuid();
        Change(id, name, mode, topic =>
        {
            topic.PersistenceWaits[token] = DateTimeOffset.UtcNow;
            topic.InFlight = topic.PersistenceWaits.Count;
            topic.PersistenceSince = topic.PersistenceWaits.Values.Min();
        });
        return token;
    }

    public static void EndPersistence(long id, string name, string mode, Guid token)
        => Change(id, name, mode, topic =>
        {
            topic.PersistenceWaits.Remove(token);
            topic.InFlight = topic.PersistenceWaits.Count;
            topic.PersistenceSince = topic.PersistenceWaits.Values.Cast<DateTimeOffset?>().Min();
        });

    public static void ResetPending(long id)
    {
        if (!Items.TryGetValue(id, out var entry)) return;
        lock (entry)
        {
            foreach (var topic in entry.Topics.Values)
            {
                topic.PendingConfirmation = 0;
                topic.ConfirmationSince = null;
            }
        }
    }

    public static void Blocked(long id, string name, string mode, Guid token)
        => Change(id, name, mode, topic => topic.Blocked.TryAdd(token, DateTimeOffset.UtcNow));

    public static void Unblocked(long id, string name, string mode, Guid token)
        => Change(id, name, mode, topic => topic.Blocked.Remove(token));

    public static void PersistenceFailed(long id, string name, string mode)
        => Change(id, name, mode, topic =>
        {
            topic.PersistenceError = true;
            Failure(topic, "persist", "persistence_failed", "日志持久化失败，消息尚未确认");
        });

    public static void Persisting(long id, string name, string mode, int delta)
        => Change(id, name, mode, topic =>
        {
            if (delta > 0 && topic.InFlight == 0) topic.PersistenceSince = DateTimeOffset.UtcNow;
            topic.InFlight = Math.Max(0, topic.InFlight + delta);
            if (topic.InFlight == 0) topic.PersistenceSince = null;
        });

    public static void Persisted(long id, string name, string mode, int count)
        => Change(id, name, mode, topic =>
        {
            topic.PersistenceError = false;
            Recover(topic, "persist");
            topic.LastProgressAt = DateTimeOffset.UtcNow;
            topic.Persisted += count;
            topic.LastPersistedAt = DateTimeOffset.UtcNow;
            Bucket(topic, topic.LastPersistedAt.Value).Records += count;
        });

    public static void Persisted(long id, int count)
    {
        var entry = Items.GetOrAdd(id, _ => new Entry());
        lock (entry)
        {
            entry.LegacyPersisted += count;
            entry.LastPersistedAt = DateTimeOffset.UtcNow;
        }
    }

    public static void Confirmed(long id, string name, string mode, int count)
        => Change(id, name, mode, topic =>
        {
            topic.Confirmed += count;
            topic.PendingConfirmation = Math.Max(0, topic.PendingConfirmation - count);
            if (topic.PendingConfirmation == 0) topic.ConfirmationSince = null;
            topic.LastProgressAt = DateTimeOffset.UtcNow;
            if (topic.PendingConfirmation == 0)
            {
                Recover(topic, "confirm");
                topic.Error = null;
            }
        });

    public static void ConfirmationFailed(long id, string name, string mode)
        => Change(id, name, mode, topic =>
        {
            topic.ConfirmationFailures++;
            topic.Error = "消息确认失败，可能发生重投递";
            Failure(topic, "confirm", "confirmation_failed", topic.Error);
        });

    public static void ConfirmationPending(long id, string name, string mode, int delta)
        => Change(id, name, mode, topic =>
        {
            if (delta > 0 && topic.PendingConfirmation == 0) topic.ConfirmationSince = DateTimeOffset.UtcNow;
            topic.PendingConfirmation = Math.Max(0, topic.PendingConfirmation + delta);
            if (topic.PendingConfirmation == 0) topic.ConfirmationSince = null;
        });

    private static void Failure(TopicEntry topic, string stage, string code, string reason)
    {
        var count = topic.Failures.TryGetValue(stage, out var previous) ? previous.Occurrences + 1 : 1;
        topic.Failures[stage] = new MqFailureItem
        {
            Stage = stage, Code = code, Reason = reason,
            At = DateTimeOffset.UtcNow, Occurrences = count,
        };
    }

    private static void Recover(TopicEntry topic, string stage)
    {
        if (topic.Failures.TryGetValue(stage, out var failure) && failure.RecoveredAt == null)
            failure.RecoveredAt = DateTimeOffset.UtcNow;
    }

    private static string Key(string name, string mode) => mode + ":" + name;

    private static void Change(long id, string name, string mode, Action<TopicEntry> action)
    {
        if (!Items.TryGetValue(id, out var entry)) return;
        lock (entry)
        {
            if (entry.Topics.TryGetValue(Key(name, mode), out var topic)) action(topic);
        }
    }

    private static WindowBucket Bucket(TopicEntry topic, DateTimeOffset now)
    {
        var second = now.ToUnixTimeSeconds();
        var bucket = topic.Window[(int)(second % 60)];
        if (bucket.Second != second)
        {
            bucket.Second = second;
            bucket.Messages = bucket.Bytes = bucket.Records = 0;
        }

        return bucket;
    }

    private static MqTopicRuntimeItem Copy(TopicEntry topic, string state, DateTimeOffset now)
    {
        var second = now.ToUnixTimeSeconds();
        var window = topic.Window.Where(b => b.Second > second - 60 && b.Second <= second).ToList();
        var messages = window.Sum(b => b.Messages);
        if (state == "connected" && topic.States.Count > 0)
            state = topic.States.Values.Any(x => x == "error") ? "error"
                : topic.States.Values.Any(x => x == "starting") ? "starting" : "connected";
        return new MqTopicRuntimeItem
        {
            Stage = topic.InFlight > 0 || topic.PersistenceError ? "persist" : topic.PendingConfirmation > 0 || topic.Error != null ? "confirm" : topic.Blocked.Count > 0 ? "parse" : state is "starting" or "error" ? "connecting" : "receive",
            StageSince = topic.InFlight > 0 ? topic.PersistenceSince : topic.PendingConfirmation > 0 ? topic.ConfirmationSince : topic.Blocked.Values.Cast<DateTimeOffset?>().Min(),
            PendingPersistence = topic.InFlight, PendingConfirmation = topic.PendingConfirmation,
            LastProgressAt = topic.LastProgressAt,
            Failures = topic.Failures.Values.Select(f => new MqFailureItem { Id = f.Id, ActualTopic = f.ActualTopic, Stage = f.Stage, Code = f.Code, Reason = f.Reason, At = f.At, RecoveredAt = f.RecoveredAt, Occurrences = f.Occurrences }).OrderByDescending(f => f.At).ToList(),
            Name = topic.Name, MatchMode = topic.Mode,
            State = topic.Blocked.Count > 0 || topic.Failures.Values.Any(f => f.RecoveredAt == null) || topic.PersistenceError ? "error" : state == "connected" ? topic.InFlight > 0 ? "persisting" : messages > 0 ? "consuming" : "idle" : state,
            MatchedTopics = topic.Matched.Count,
            ReceivedMessages = topic.Received, ReceivedBytes = topic.Bytes,
            PersistedRecords = topic.Persisted, ParseFailures = topic.ParseFailures,
            BlockedMessages = topic.Blocked.Count, BlockedSince = topic.Blocked.Values.Cast<DateTimeOffset?>().Min(),
            ConfirmedMessages = topic.Confirmed,
            ConfirmationFailures = topic.ConfirmationFailures,
            MessagesPerSecond = messages / 60d,
            RecordsPerSecond = window.Sum(b => b.Records) / 60d,
            BytesPerSecond = window.Sum(b => b.Bytes) / 60d,
            LastPersistedAt = topic.LastPersistedAt, LastError = topic.PersistenceError ? "日志持久化失败，请检查存储日志" : topic.Error,
        };
    }

    private sealed class Entry
    {
        public int RetryAttempt;
        public DateTimeOffset? NextRetryAt;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public string State { get; set; } = "starting";
        public string? Error { get; set; }
        public long LegacyPersisted { get; set; }
        public DateTimeOffset? LastPersistedAt { get; set; }
        public Dictionary<string, TopicEntry> Topics { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TopicEntry(string name, string mode)
    {
        public string Name { get; } = name;
        public string Mode { get; } = mode;
        public HashSet<string> Matched { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);
        public WindowBucket[] Window { get; } = Enumerable.Range(0, 60).Select(_ => new WindowBucket()).ToArray();
        public long Received, Bytes, Persisted, ParseFailures, Confirmed, ConfirmationFailures;
        public Dictionary<Guid, DateTimeOffset> Blocked { get; } = new();
        public Dictionary<Guid, DateTimeOffset> PersistenceWaits { get; } = new();
        public int InFlight;
        public int PendingConfirmation;
        public DateTimeOffset? PersistenceSince, ConfirmationSince, LastProgressAt;
        public Dictionary<string, MqFailureItem> Failures { get; } = new(StringComparer.Ordinal);
        public bool PersistenceError;
        public DateTimeOffset? LastPersistedAt;
        public string? Error;
    }

    private sealed class WindowBucket
    {
        public long Second, Messages, Bytes, Records;
    }
}
