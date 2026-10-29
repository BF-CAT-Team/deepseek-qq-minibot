namespace Bot.Core.Memory;

/// <summary>文档 3.6 的 DDL。除原文列之外只加了两处：<c>chat.kind</c> 与 <c>person.union_open_id</c>（见注释）。</summary>
public static class Schema
{
    public const int CurrentVersion = 1;

    public const string Ddl = """
        -- 群/会话配置
        CREATE TABLE IF NOT EXISTS chat (
          chat_id      TEXT PRIMARY KEY,
          kind         TEXT NOT NULL DEFAULT 'group',   -- group|direct（v1.2 补充：区分群与单聊）
          display_name TEXT,
          preset       TEXT NOT NULL DEFAULT 'balanced',
          config_json  TEXT NOT NULL DEFAULT '{}',
          paused       INTEGER NOT NULL DEFAULT 0,
          created_at   INTEGER NOT NULL
        );

        -- 人物记忆
        CREATE TABLE IF NOT EXISTS person (
          person_id     INTEGER PRIMARY KEY AUTOINCREMENT,
          open_id       TEXT    NOT NULL DEFAULT '',
          union_open_id TEXT,                            -- 跨群统一标识（QQ 群事件给的是群维度 member_openid）
          name          TEXT    NOT NULL DEFAULT '',
          name_source   TEXT    NOT NULL DEFAULT 'none',
          display_label TEXT    NOT NULL DEFAULT '',
          personality   TEXT    NOT NULL DEFAULT '',
          evaluation    TEXT    NOT NULL DEFAULT '',
          memory_text   TEXT    NOT NULL DEFAULT '',
          importance    INTEGER NOT NULL DEFAULT 3,
          pinned        INTEGER NOT NULL DEFAULT 0,
          status        TEXT    NOT NULL DEFAULT 'active',
          chat_scope    TEXT    NOT NULL DEFAULT '',
          msg_count     INTEGER NOT NULL DEFAULT 0,
          first_seen_at INTEGER,
          created_at    INTEGER NOT NULL,
          updated_at    INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_person_openid ON person(open_id);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_person_union ON person(union_open_id) WHERE union_open_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_person_status ON person(status, updated_at);

        CREATE TABLE IF NOT EXISTS person_alias (
          alias     TEXT NOT NULL,
          person_id INTEGER NOT NULL REFERENCES person(person_id) ON DELETE CASCADE,
          source    TEXT NOT NULL DEFAULT 'learned',
          hits      INTEGER NOT NULL DEFAULT 1,
          PRIMARY KEY (alias, person_id)
        );

        -- open_id → person 的多对一映射（QQ 群的 member_openid 在不同群可能不同，
        -- 同一人靠 union_openid 聚合到同一 person，这里登记全部见过的 open_id）
        CREATE TABLE IF NOT EXISTS person_open_id (
          open_id       TEXT PRIMARY KEY,
          person_id     INTEGER NOT NULL REFERENCES person(person_id) ON DELETE CASCADE,
          first_seen_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_poid_person ON person_open_id(person_id);

        CREATE TABLE IF NOT EXISTS person_label (
          open_id  TEXT NOT NULL,
          chat_id  TEXT NOT NULL,
          label    TEXT NOT NULL,
          source   TEXT NOT NULL DEFAULT 'auto',
          PRIMARY KEY (open_id, chat_id)
        );

        -- 事件记忆
        CREATE TABLE IF NOT EXISTS event (
          event_id       INTEGER PRIMARY KEY AUTOINCREMENT,
          time_text      TEXT    NOT NULL DEFAULT '',
          time_norm      INTEGER,
          location       TEXT    NOT NULL DEFAULT '',      -- 作用域键：chat_id（用于按群隔离）
          location_label TEXT    NOT NULL DEFAULT '',      -- 展示名：群名（v1.2 补充，避免把不透明 chat_id 写进 prompt）
          detail         TEXT    NOT NULL,
          importance     INTEGER NOT NULL DEFAULT 3,
          pinned         INTEGER NOT NULL DEFAULT 0,
          status         TEXT    NOT NULL DEFAULT 'active',
          last_used_at   INTEGER,
          injected_count INTEGER NOT NULL DEFAULT 0,
          content_hash   TEXT    NOT NULL,
          created_at     INTEGER NOT NULL,
          archived_at    INTEGER
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_event_hash ON event(content_hash);
        CREATE INDEX IF NOT EXISTS ix_event_pick ON event(status, location, last_used_at);

        CREATE TABLE IF NOT EXISTS event_person (
          event_id  INTEGER NOT NULL REFERENCES event(event_id) ON DELETE CASCADE,
          person_id INTEGER NOT NULL REFERENCES person(person_id) ON DELETE CASCADE,
          PRIMARY KEY (event_id, person_id)
        );

        CREATE TABLE IF NOT EXISTS event_keyword (
          event_id INTEGER NOT NULL REFERENCES event(event_id) ON DELETE CASCADE,
          word     TEXT    NOT NULL,
          PRIMARY KEY (event_id, word)
        );
        CREATE INDEX IF NOT EXISTS ix_kw_word ON event_keyword(word);

        CREATE VIRTUAL TABLE IF NOT EXISTS event_fts USING fts5(
          detail, content='event', content_rowid='event_id', tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS event_ai AFTER INSERT ON event BEGIN
          INSERT INTO event_fts(rowid, detail) VALUES (new.event_id, new.detail);
        END;
        CREATE TRIGGER IF NOT EXISTS event_ad AFTER DELETE ON event BEGIN
          INSERT INTO event_fts(event_fts, rowid, detail) VALUES('delete', old.event_id, old.detail);
        END;
        CREATE TRIGGER IF NOT EXISTS event_au AFTER UPDATE ON event BEGIN
          INSERT INTO event_fts(event_fts, rowid, detail) VALUES('delete', old.event_id, old.detail);
          INSERT INTO event_fts(rowid, detail) VALUES (new.event_id, new.detail);
        END;

        -- 会话
        CREATE TABLE IF NOT EXISTS session (
          session_id    TEXT PRIMARY KEY,
          chat_id       TEXT    NOT NULL,
          started_at    INTEGER NOT NULL,
          ended_at      INTEGER,
          end_reason    TEXT,
          turn_count    INTEGER NOT NULL DEFAULT 0,
          token_in      INTEGER NOT NULL DEFAULT 0,
          token_cached  INTEGER NOT NULL DEFAULT 0,
          token_out     INTEGER NOT NULL DEFAULT 0,
          prefix_hash   TEXT,
          prefix_text   TEXT,
          reply_target  TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_session_chat ON session(chat_id, started_at);

        -- 会话回合（v1.2 补充）：崩溃后要能"补总结"，只靠内存会丢整段对话。
        CREATE TABLE IF NOT EXISTS session_turn (
          session_id TEXT NOT NULL,
          seq        INTEGER NOT NULL,
          role       TEXT NOT NULL,
          content    TEXT NOT NULL,
          kind       TEXT NOT NULL DEFAULT 'history',
          PRIMARY KEY (session_id, seq)
        );

        -- 请求日志（成本与缓存分析的唯一事实来源）
        CREATE TABLE IF NOT EXISTS request_log (
          request_id        TEXT PRIMARY KEY,
          session_id        TEXT NOT NULL,
          chat_id           TEXT NOT NULL,
          seq               INTEGER NOT NULL,
          kind              TEXT NOT NULL,
          model             TEXT NOT NULL,
          sent_at           INTEGER NOT NULL,
          prefix_hash       TEXT NOT NULL,
          prefix_tokens     INTEGER,
          history_tokens    INTEGER,
          tail_tokens       INTEGER,
          prompt_tokens     INTEGER,
          cache_hit_tokens  INTEGER,
          cache_miss_tokens INTEGER,
          completion_tokens INTEGER,
          latency_ms        INTEGER,
          parse_ok          INTEGER,
          retry_of          TEXT,
          idem_key          TEXT UNIQUE,
          send_confirmed    INTEGER NOT NULL DEFAULT 0,
          error_code        TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_req_chat_time ON request_log(chat_id, sent_at);

        -- 记忆操作审计（支持整批回滚）
        CREATE TABLE IF NOT EXISTS memory_op_log (
          op_id       INTEGER PRIMARY KEY AUTOINCREMENT,
          batch_id    TEXT NOT NULL,
          op          TEXT NOT NULL,
          tbl         TEXT NOT NULL,
          row_id      INTEGER NOT NULL,
          before_json TEXT,
          after_json  TEXT,
          source      TEXT NOT NULL,
          created_at  INTEGER NOT NULL,
          reverted    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_op_batch ON memory_op_log(batch_id);

        -- 崩溃恢复
        CREATE TABLE IF NOT EXISTS wal_pending (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL,
          kind       TEXT NOT NULL,
          payload    TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          retries    INTEGER NOT NULL DEFAULT 0,
          last_error TEXT
        );

        -- 入站消息缓冲（文档 3.1.1 的 message_buffer）
        CREATE TABLE IF NOT EXISTS message (
          message_id   TEXT PRIMARY KEY,
          chat_id      TEXT NOT NULL,
          open_id      TEXT NOT NULL,
          sender_label TEXT NOT NULL DEFAULT '',
          content      TEXT NOT NULL,
          at           INTEGER NOT NULL,
          mentions_bot INTEGER NOT NULL DEFAULT 0,
          reply_to     TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_message_chat_at ON message(chat_id, at);

        -- 名字学习计数（他人称呼 N 次才认定）
        CREATE TABLE IF NOT EXISTS name_evidence (
          open_id    TEXT NOT NULL,
          chat_id    TEXT NOT NULL,
          candidate  TEXT NOT NULL,
          hits       INTEGER NOT NULL DEFAULT 1,
          updated_at INTEGER NOT NULL,
          PRIMARY KEY (open_id, chat_id, candidate)
        );

        CREATE TABLE IF NOT EXISTS schema_meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        -- 群级运行时状态（v1.2 补充）：上轮摘要与冷却时间需要跨重启保留
        CREATE TABLE IF NOT EXISTS chat_state (
          chat_id        TEXT PRIMARY KEY,
          last_summary   TEXT NOT NULL DEFAULT '',
          cooldown_until INTEGER,
          updated_at     INTEGER NOT NULL
        );
        """;
}
