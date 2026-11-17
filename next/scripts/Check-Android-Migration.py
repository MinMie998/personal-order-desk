"""Exercise actual migration SQL on synthetic SQLite; not an Android Room runtime test."""
from pathlib import Path
import hashlib
import json
import re
import sqlite3
import uuid

root = Path(__file__).resolve().parents[2]
source = (root / "next/android/app/src/main/java/com/personalorderdesk/nativepreview/DatabaseMigration.kt").read_text(encoding="utf-8")
schema = json.loads((root / "next/android/app/schemas/com.personalorderdesk.nativepreview.WorkbenchDatabase/2.json").read_text(encoding="utf-8"))["database"]
workspace = "11111111-1111-4111-8111-111111111111"

def fixture():
    db = sqlite3.connect(":memory:", isolation_level=None)
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript("""
    CREATE TABLE orders(id TEXT NOT NULL PRIMARY KEY,number TEXT NOT NULL,title TEXT NOT NULL,kind TEXT NOT NULL,status TEXT NOT NULL,acceptedDate TEXT NOT NULL,dueDate TEXT,completedDate TEXT,quoteCents INTEGER NOT NULL,progress TEXT NOT NULL,nextStep TEXT NOT NULL,projectPath TEXT NOT NULL,notes TEXT NOT NULL,trackingNumber TEXT NOT NULL,carrier TEXT NOT NULL,shippedDate TEXT,deliveredDate TEXT,parentId TEXT,revision INTEGER NOT NULL,deletedAt TEXT);
    CREATE TABLE money(id TEXT NOT NULL PRIMARY KEY,orderId TEXT NOT NULL,kind TEXT NOT NULL,amountCents INTEGER NOT NULL,date TEXT,method TEXT NOT NULL,note TEXT NOT NULL,voidedAt TEXT,voidReason TEXT,FOREIGN KEY(orderId) REFERENCES orders(id) ON DELETE RESTRICT);
    CREATE INDEX index_money_orderId ON money(orderId);
    CREATE TABLE history(id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,entityId TEXT NOT NULL,action TEXT NOT NULL,at TEXT NOT NULL,payload TEXT NOT NULL);
    """)
    db.execute("INSERT INTO orders VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",("order","SYNTHETIC","旧项目","physical","done","2026-11-15",None,None,9007199254740993,"已交付","","D:/project","备注","","",None,None,None,7,None))
    for mid, voided in [("receipt-a",None),("receipt-b","2026-11-16T12:00:00Z")]:
        db.execute("INSERT INTO money VALUES(?,?,?,?,?,?,?,?,?)",(mid,"order","receipt",5000,None,"微信","",voided,"核销" if voided else None))
    db.execute("INSERT INTO history(entityId,action,at,payload) VALUES('order','save-order','2026-11-15T12:00:00Z','{}')")
    return db

def migrate(db):
    # Execute strings from the Kotlin migration, including parameterized updates.
    statements = re.findall(r'db\.execSQL\("([^"\n]+)"', source.split("fun migrationV2ToV3")[0])
    assert len(statements) == 8, "Review new migration statements and extend this check"
    for sql in statements:
        if sql.startswith("UPDATE history SET stableId"):
            for (hid,) in db.execute("SELECT id FROM history").fetchall():
                digest = hashlib.md5(f"{workspace}:history:{hid}".encode()).digest()
                db.execute(sql,(str(uuid.UUID(bytes=digest,version=3)),hid))
        elif "?" in sql:
            db.execute(sql,(workspace,))
        else:
            db.execute(sql)

def table(db,name):
    return db.execute(f"PRAGMA table_info(`{name}`)").fetchall()

before = fixture()
originals = {name: before.execute(f"SELECT * FROM {name}").fetchall() for name in ("orders","money","history")}
old_columns = {name: [r[1] for r in table(before,name)] for name in originals}
before.execute("BEGIN")
migrate(before)
for name,rows in originals.items():
    cols=",".join(f"`{c}`" for c in old_columns[name])
    assert before.execute(f"SELECT {cols} FROM {name}").fetchall()==rows, name
assert before.execute("SELECT dueDate,completedDate,historicalDates,tasks FROM orders").fetchone()==(None,None,1,"[]")
assert before.execute("SELECT count(*),sum(amountCents) FROM money WHERE voidedAt IS NULL").fetchone()==(1,5000)
assert before.execute("SELECT workspaceId,generation FROM workspace_meta").fetchone()==(workspace,"legacy")
assert before.execute("PRAGMA integrity_check").fetchone()==("ok",)
assert before.execute("PRAGMA foreign_key_check").fetchall()==[]

expected=sqlite3.connect(":memory:")
for entity in schema["entities"]:
    name=entity["tableName"]
    expected.execute(entity["createSql"].replace("${TABLE_NAME}",name))
    assert table(before,name)==table(expected,name), f"Room exported schema differs: {name}"
assert before.execute("PRAGMA index_info(index_history_stableId)").fetchone()[2]=="stableId"
before.execute("ROLLBACK")
assert "tasks" not in [r[1] for r in table(before,"orders")]
assert before.execute("SELECT * FROM orders").fetchall()==originals["orders"]
print("PASS: v1 rows/IDs/money preserved; null dates retained; schema matches Room v2; transaction rollback intact.")
print("Scope: host SQLite SQL only; Android Room migration and fsync backup still require device verification.")

# A second isolated transaction checks the new v2 -> v3 statements against Room v3.
v2=fixture();v2.execute("BEGIN");migrate(v2);v2.execute("COMMIT")
v2.execute("UPDATE orders SET tasks=?", ('[]',))
prior={name:v2.execute(f"SELECT * FROM {name}").fetchall() for name in originals}
v2.execute("BEGIN")
for sql in re.findall(r'db\.execSQL\("([^"\n]+)"', source.split("fun migrationV2ToV3",1)[1]):
    v2.execute(sql)
schema3=json.loads((root / "next/android/app/schemas/com.personalorderdesk.nativepreview.WorkbenchDatabase/3.json").read_text(encoding="utf-8"))["database"]
expected3=sqlite3.connect(":memory:")
for entity in schema3["entities"]:
    name=entity["tableName"];expected3.execute(entity["createSql"].replace("${TABLE_NAME}",name))
    assert table(v2,name)==table(expected3,name),f"Room v3 schema differs: {name}"
for name,rows in prior.items():assert v2.execute(f"SELECT * FROM {name}").fetchall()==rows
assert v2.execute("SELECT count(*) FROM sync_state").fetchone()==(0,)
assert v2.execute("SELECT schemaVersion FROM workspace_meta").fetchone()==(3,)
v2.execute("ROLLBACK")
assert v2.execute("SELECT schemaVersion FROM workspace_meta").fetchone()==(2,)
assert v2.execute("SELECT name FROM sqlite_master WHERE name='sync_state'").fetchall()==[]
print("PASS: v2 -> v3 preserves records, matches Room v3 and rolls back metadata/table changes.")
