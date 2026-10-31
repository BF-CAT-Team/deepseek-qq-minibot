import sqlite3
import sys

con = sqlite3.connect(sys.argv[1])
c = con.cursor()

total = c.execute(
    "select count(*), coalesce(sum(prompt_tokens),0), coalesce(sum(cache_hit_tokens),0), "
    "coalesce(sum(cache_miss_tokens),0), coalesce(sum(completion_tokens),0) from request_log"
).fetchone()

print("requests=%d prompt=%d hit=%d miss=%d completion=%d" % total)
print("hit_ratio=%.3f" % (total[2] / max(1, total[2] + total[3])))
print(
    "chats=%d persons=%d events=%d sessions=%d" % (
        c.execute("select count(distinct chat_id) from request_log").fetchone()[0],
        c.execute("select count(*) from person").fetchone()[0],
        c.execute("select count(*) from event").fetchone()[0],
        c.execute("select count(*) from session").fetchone()[0],
    )
)
print("prefix_tokens min/max/avg=%s" % (c.execute(
    "select min(prefix_tokens), max(prefix_tokens), round(avg(prefix_tokens),1) from request_log"
).fetchone(),))
print("send_confirmed=%s/%s" % c.execute(
    "select coalesce(sum(send_confirmed),0), count(*) from request_log"
).fetchone())
print("summary parse_ok=%s/%s" % c.execute(
    "select coalesce(sum(parse_ok),0), count(*) from request_log where kind='summary'"
).fetchone())

print("--- 每群 prefix 增长 ---")
for row in c.execute(
    "select chat_id, count(*), min(prefix_tokens), max(prefix_tokens) from request_log "
    "where kind='chat' group by chat_id"
):
    print("   ", row[0][:8] + "...", "req=%d" % row[1], "prefix %d→%d" % (row[2], row[3]))

con.close()
