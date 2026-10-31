"""导出某个群最近的消息时间线，用于排查"上下文里混进了旧消息"。

用法：python tools/timeline.py <bot.db> <chat_id 前缀> <输出文件> [条数]
"""

import datetime
import sqlite3
import sys


def main(db_path: str, chat_prefix: str, out_path: str, limit: int = 60) -> None:
    con = sqlite3.connect(db_path)
    rows = list(
        con.execute(
            "select chat_id, at, content, sender_label from message "
            "where chat_id like ? order by at desc limit ?",
            (chat_prefix + "%", limit),
        )
    )
    lines = []
    for chat_id, at, content, label in reversed(rows):
        when = datetime.datetime.fromtimestamp(at).strftime("%m-%d %H:%M:%S")
        lines.append(f"{when}  [{label}] {(content or '')[:48]}")

    con.close()
    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))
    print(f"written {len(lines)} rows")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 60)
