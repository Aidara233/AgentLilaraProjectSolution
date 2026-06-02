"""
从备份库全量迁移到主库TempMemories（排除主库已存在的）
"""
import sqlite3

BACKUP = r"E:\Workspace\AgentLilaraProject\Storage\Database.Copy\lilara.db"
MAIN = r"E:\Workspace\AgentLilaraProject\Storage\Database\lilara.db"

bk = sqlite3.connect(f"file:{BACKUP}?mode=ro", uri=True)
main = sqlite3.connect(MAIN)

# 主库已有内容
existing_contents = set(row[0] for row in main.execute("SELECT Content FROM Memories"))
existing_temp = set(row[0] for row in main.execute("SELECT Content FROM TempMemories"))
existing_all = existing_contents | existing_temp

cols = [desc[0] for desc in bk.execute("SELECT * FROM Memories LIMIT 0").description]
col_idx = {name: i for i, name in enumerate(cols)}

all_rows = bk.execute("SELECT * FROM Memories").fetchall()
candidates = [r for r in all_rows if r[col_idx["Content"]] not in existing_all]
print(f"备份库 {len(all_rows)} 条，主库 {len(existing_contents)} 条，Temp {len(existing_temp)} 条，可迁移 {len(candidates)} 条")

def certainty_to_confidence(certainty):
    if certainty is None:
        return "medium"
    c = float(certainty)
    return "high" if c >= 0.7 else "low" if c <= 0.3 else "medium"

BATCH = 500
inserted = 0
for i in range(0, len(candidates), BATCH):
    batch = candidates[i:i+BATCH]
    for row in batch:
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
    print(f"  已写入 {inserted}/{len(candidates)}")

temp_count = main.execute("SELECT COUNT(*) FROM TempMemories").fetchone()[0]
mem_count = main.execute("SELECT COUNT(*) FROM Memories").fetchone()[0]
print(f"完成 — TempMemories: {temp_count}, Memories: {mem_count}")

bk.close()
main.close()
