import sqlite3
import time

create_table_sql = """
CREATE TABLE "patch_cache" (
	"from_sha256" TEXT,
	"to_sha256" TEXT,
	"patch_type" TEXT,
	"patch_size" INTEGER,
    "timestamp" INTEGER
);
CREATE INDEX "cache_index" ON "patch_cache" (
	"from_sha256",
	"to_sha256",
	"patch_type"
);
"""

class PatchCache:
    def __init__(self, db_path):
        self.db_path = db_path
        self.conn = sqlite3.connect(db_path)
        cursor = self.conn.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='patch_cache';")
        table_exists = cursor.fetchone()
        if not table_exists:
            with self.conn:
                self.conn.executescript(create_table_sql)

    def add_patch(self, from_sha256: str, to_sha256: str, patch_type: str, patch_size: int, timestamp: int | None = None):
        if timestamp is None:
            timestamp = int(time.time())
        with self.conn:
            self.conn.execute("INSERT INTO patch_cache VALUES (?, ?, ?, ?, ?)", (from_sha256, to_sha256, patch_type, patch_size, timestamp))

    def query(self, from_sha256: str, to_sha256: str, patch_type: str) -> int | None:
        cursor = self.conn.execute("SELECT patch_size FROM patch_cache WHERE from_sha256 = ? AND to_sha256 = ? AND patch_type = ?;", (from_sha256, to_sha256, patch_type))
        result = cursor.fetchone()
        if result is None:
            return None
        return result[0]

    def close(self):
        self.conn.close()

