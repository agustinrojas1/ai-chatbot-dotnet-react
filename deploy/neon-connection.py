"""Turns the postgresql:// URI that Neon shows into the key=value format Npgsql expects.

    python3 deploy/neon-connection.py 'postgresql://user:password@host/db?sslmode=require'
    python3 deploy/neon-connection.py            # reads DATABASE_URL from the environment or .env.production
"""
import os
import sys
import urllib.parse as u
from pathlib import Path


def database_url() -> str:
    if len(sys.argv) > 1:
        return sys.argv[1]
    if os.environ.get("DATABASE_URL"):
        return os.environ["DATABASE_URL"]
    env = Path(__file__).resolve().parent.parent / ".env.production"
    if env.exists():
        for line in env.read_text().splitlines():
            if line.startswith("DATABASE_URL="):
                return line.split("=", 1)[1].strip().strip("'\"")
    sys.exit("No connection string: pass it as an argument or set DATABASE_URL in .env.production.")


p = u.urlparse(database_url())
if p.scheme not in ("postgres", "postgresql") or not p.hostname:
    sys.exit("Expected a postgresql://user:password@host/database URI.")
q = dict(u.parse_qsl(p.query))
parts = {
    "Host": p.hostname,
    "Port": p.port or 5432,
    "Database": p.path.lstrip("/"),
    "Username": u.unquote(p.username or ""),
    "Password": u.unquote(p.password or ""),
    "SSL Mode": "Disable" if q.get("sslmode") == "disable" else "Require",
    "Maximum Pool Size": 10,
    "Timeout": 30,
}


def quote(value: object) -> str:
    """DbConnectionStringBuilder rules: a value with ; = or quotes goes between double quotes, inner ones doubled."""
    s = str(value)
    return '"' + s.replace('"', '""') + '"' if any(c in s for c in ';="\'') or s != s.strip() else s


print(";".join(f"{k}={quote(v)}" for k, v in parts.items()))
