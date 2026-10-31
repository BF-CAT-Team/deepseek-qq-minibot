"""一次性数据修复：把事件里被写进去的群 open_id 片段清掉。

背景：QQ 不下发群名，早期版本用 chat_id 兜底当群名，
导致 location_label 里出现 "群7CFF2FD251E3D14DC6C" 这种不透明串，
它会随记忆块进入每一轮前缀。

用法：python tools/fix_event_labels.py <bot.db>
"""

import sqlite3
import sys

OVERRIDE = "本群"


def main(path: str) -> None:
    con = sqlite3.connect(path)
    cur = con.cursor()

    print("修复前：")
    for row in cur.execute(
        "select event_id, location_label from event where length(location_label) > 6"
    ):
        print("  ", row[0], repr(row[1]))

    cur.execute(
        "update event set location_label = ? "
        "where location_label like '群%' and length(location_label) > 6",
        (OVERRIDE,),
    )
    con.commit()

    print("修复后：")
    for row in cur.execute("select event_id, location_label from event"):
        print("  ", row[0], repr(row[1]))

    con.close()


if __name__ == "__main__":
    main(sys.argv[1])
