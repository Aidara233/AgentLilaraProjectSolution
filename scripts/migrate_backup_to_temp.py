"""
从备份库随机挑100条记忆 → 清空主库Memories/MemoryLinks → 写入TempMemories
"""
import sqlite3
import random
import os
import shutil

BACKUP = r"E:\Workspace\AgentLilaraProject\Storage\Database.Copy\lilara.db"
MAIN = r"E:\Workspace\AgentLilaraProject\Storage\Database\lilara.db"
COUNT = 100

# 0. 备份主库（安全措施）
backup_path = MAIN + ".pre_migrate.bak"
shutil.copy2(MAIN, backup_path)
print(f"主库已备份: {backup_path}")

# 1. 从备份库查表结构
bk = sqlite3.connect(f"file:{BACKUP}?mode=ro", uri=True)
cur = bk.execute("SELECT sql FROM sqlite_master WHERE name='Memories'")
print("备份库 Memories 表结构:")
print(cur.fetchone()[0])

# 查总数
count = bk.execute("SELECT COUNT(*) FROM Memories").fetchone()[0]
print(f"备份库共 {count} 条记忆")

# 2. 随机挑100条
ids = [row[0] for row in bk.execute("SELECT Id FROM Memories")]
picked = sorted(random.sample(ids, min(COUNT, len(ids))))
print(f"随机选取 {len(picked)} 条: {picked[:10]}...")

# 获取列名
cols = [desc[0] for desc in bk.execute("SELECT * FROM Memories LIMIT 0").description]
print(f"列: {cols}")

# 读取选中行
rows = []
for mid in picked:
    row = bk.execute(f"SELECT * FROM Memories WHERE Id = ?", (mid,)).fetchone()
    rows.append(row)

bk.close()
print(f"读取 {len(rows)} 条完成")

# 3. 操作主库 - 清空并写入临时记忆
main = sqlite3.connect(MAIN)
main.execute("PRAGMA journal_mode=WAL")
main.execute("PRAGMA foreign_keys=OFF")

# 清空
main.execute("DELETE FROM MemoryLinks")
main.execute("DELETE FROM Memories")
main.execute("DELETE FROM TempMemories")
print("已清空 Memories, MemoryLinks, TempMemories")

# 构建列名到索引的映射
col_idx = {name: i for i, name in enumerate(cols)}

def certainty_to_confidence(certainty):
    """float Certainty → string Confidence"""
    if certainty is None:
        return "medium"
    c = float(certainty)
    if c >= 0.7:
        return "high"
    elif c <= 0.3:
        return "low"
    else:
        return "medium"

# 写入 TempMemories
inserted = 0
for row in rows:
    content = row[col_idx["Content"]]
    mem_type = row[col_idx["Type"]]
    subject = row[col_idx["Subject"]]
    embedding = row[col_idx["Embedding"]]
    person_id = row[col_idx["PersonId"]]
    channel_id = row[col_idx["ChannelId"]]
    certainty = row[col_idx["Certainty"]]
    source_msg = row[col_idx["SourceMessageId"]]

    confidence = certainty_to_confidence(certainty)

    main.execute(
        """INSERT INTO TempMemories
           (Content, Type, Subject, Embedding, PersonId, ChannelId, Confidence, SourceMessageId, CreatedAt)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, datetime('now'))""",
        (content, mem_type, subject, embedding, person_id, channel_id, confidence, source_msg)
    )
    inserted += 1

main.commit()
print(f"写入 TempMemories: {inserted} 条")

# 验证
temp_count = main.execute("SELECT COUNT(*) FROM TempMemories").fetchone()[0]
mem_count = main.execute("SELECT COUNT(*) FROM Memories").fetchone()[0]
link_count = main.execute("SELECT COUNT(*) FROM MemoryLinks").fetchone()[0]
print(f"验证 — TempMemories: {temp_count}, Memories: {mem_count}, MemoryLinks: {link_count}")

main.close()
print("完成")
