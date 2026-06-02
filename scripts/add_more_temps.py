"""
从备份库随机挑100条 → 写入主库TempMemories（不清空Memories/MemoryLinks）
"""
import sqlite3
import random

BACKUP = r"E:\Workspace\AgentLilaraProject\Storage\Database.Copy\lilara.db"
MAIN = r"E:\Workspace\AgentLilaraProject\Storage\Database\lilara.db"
COUNT = 100

bk = sqlite3.connect(f"file:{BACKUP}?mode=ro", uri=True)

# 排除主库已存在的（按Content精确匹配）
main = sqlite3.connect(MAIN)
existing_contents = set(row[0] for row in main.execute("SELECT Content FROM Memories"))
existing_temp = set(row[0] for row in main.execute("SELECT Content FROM TempMemories"))
existing_all = existing_contents | existing_temp

total = bk.execute("SELECT COUNT(*) FROM Memories").fetchone()[0]
print(f"备份库共 {total} 条，主库已有 {len(existing_contents)} 条，Temp已有 {len(existing_temp)} 条")

# 随机挑100条，排除已存在的
cols = [desc[0] for desc in bk.execute("SELECT * FROM Memories LIMIT 0").description]
col_idx = {name: i for i, name in enumerate(cols)}

all_rows = bk.execute("SELECT * FROM Memories").fetchall()
candidates = [r for r in all_rows if r[col_idx["Content"]] not in existing_all]
print(f"可选候选: {len(candidates)} 条")

picked = random.sample(candidates, min(COUNT, len(candidates)))
print(f"选取 {len(picked)} 条")

def certainty_to_confidence(certainty):
    if certainty is None:
        return "medium"
    c = float(certainty)
    if c >= 0.7:
        return "high"
    elif c <= 0.3:
        return "low"
    else:
        return "medium"

inserted = 0
for row in picked:
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

temp_count = main.execute("SELECT COUNT(*) FROM TempMemories").fetchone()[0]
mem_count = main.execute("SELECT COUNT(*) FROM Memories").fetchone()[0]
print(f"写入 {inserted} 条 → TempMemories: {temp_count}, Memories: {mem_count}")

bk.close()
main.close()
print("完成")
