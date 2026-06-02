-- 2026-06-02: ReviewHints 表重命名为 Beacons
-- 执行方式：sqlite3 <数据库路径> < 此文件

-- 1. 创建新表
CREATE TABLE IF NOT EXISTS Beacons (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Content TEXT NOT NULL DEFAULT '',
    MessageId INTEGER,
    PersonId INTEGER,
    ChannelId INTEGER,
    Source TEXT NOT NULL DEFAULT 'model',
    Consumer TEXT NOT NULL DEFAULT 'review',
    IsProcessed INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    ProcessedAt TEXT
);

-- 2. 迁移数据
INSERT INTO Beacons (Id, Content, MessageId, PersonId, ChannelId, Source, IsProcessed, CreatedAt)
SELECT Id, Content, MessageId, PersonId, ChannelId, Source, IsProcessed, CreatedAt
FROM ReviewHints;

-- 3. 删除旧表
DROP TABLE ReviewHints;
