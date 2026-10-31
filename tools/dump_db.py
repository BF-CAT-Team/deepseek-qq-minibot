import sqlite3
import sys

db = sys.argv[1]
out = sys.argv[2]
con = sqlite3.connect(db)
lines = []

lines.append("--- request_log ---")
for row in con.execute(
    "select seq, kind, prefix_hash, prefix_tokens, history_tokens, tail_tokens, "
    "prompt_tokens, cache_hit_tokens, cache_miss_tokens, parse_ok, send_confirmed, error_code "
    "from request_log order by sent_at"
):
    lines.append(str(row))

lines.append("--- distinct chat prefix hashes ---")
for row in con.execute("select distinct prefix_hash from request_log where kind='chat'"):
    lines.append(str(row[0]))

lines.append("--- messages ---")
for row in con.execute("select sender_label, content, mentions_bot from message order by at"):
    lines.append(str(row))

lines.append("--- sessions ---")
for row in con.execute(
    "select session_id, ended_at is null as open, end_reason, turn_count, prefix_hash is not null from session"
):
    lines.append(str(row))

lines.append("--- events ---")
for row in con.execute("select event_id, time_text, location, location_label, detail, status from event"):
    lines.append(str(row))

lines.append("--- person ---")
for row in con.execute(
    "select person_id, name, display_label, memory_text, msg_count, importance, pinned from person"
):
    lines.append(str(row))

lines.append("--- memory_op_log ---")
for row in con.execute("select batch_id, op, tbl, row_id, reverted from memory_op_log"):
    lines.append(str(row))

with open(out, "w", encoding="utf-8") as fh:
    fh.write("\n".join(lines))
print("written", out)
