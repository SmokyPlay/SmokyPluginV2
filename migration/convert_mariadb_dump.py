#!/usr/bin/env python3
"""Convert a SmokyPluginV2 MariaDB dump into an id-preserving PostgreSQL script."""

from __future__ import annotations

import argparse
import gzip
import re
import sys
import tempfile
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Optional, Sequence, TextIO, Tuple


TABLE_ORDER = (
    "schema_migrations",
    "servers",
    "players",
    "account_links",
    "referrals",
    "warnings",
    "warning_sequences",
    "player_statistics",
    "server_statistics",
    "legacy_imports",
)
DEPRECATED_TABLES = {"player_privileges"}
BOOLEAN_COLUMNS = {("players", "statistics_private")}
REQUIRED_SOURCE_TABLES = {"servers", "players"}

CREATE_START_RE = re.compile(r"^CREATE TABLE(?: IF NOT EXISTS)?\s+`(?P<table>[^`]+)`", re.IGNORECASE)
INSERT_START_RE = re.compile(r"^INSERT INTO\s+`(?P<table>[^`]+)`", re.IGNORECASE)
CREATE_TABLE_RE = re.compile(
    r"^CREATE TABLE(?: IF NOT EXISTS)?\s+`(?P<table>[^`]+)`\s*\((?P<body>.*)\)\s*[^;]*;\s*$",
    re.IGNORECASE | re.DOTALL,
)
INSERT_RE = re.compile(
    r"^INSERT INTO\s+`(?P<table>[^`]+)`(?:\s*\((?P<columns>[^)]*)\))?\s+VALUES\s*(?P<body>.*);\s*$",
    re.IGNORECASE | re.DOTALL,
)
MYSQL_COLUMN_RE = re.compile(r"^\s*`(?P<column>[^`]+)`", re.MULTILINE)
PG_TABLE_RE = re.compile(
    r"CREATE TABLE IF NOT EXISTS\s+(?P<table>[a-z_][a-z0-9_]*)\s*\((?P<body>.*?)\n\);",
    re.IGNORECASE | re.DOTALL,
)
PG_COLUMN_RE = re.compile(r"^\s{4}(?P<column>[a-z_][a-z0-9_]*)\s+", re.MULTILINE)
NUMBER_RE = re.compile(r"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?$")


class ConversionError(RuntimeError):
    pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Convert a MariaDB mysqldump for SmokyPluginV2 to PostgreSQL SQL."
    )
    parser.add_argument("dump", type=Path, help="Source .sql or .sql.gz MariaDB dump")
    parser.add_argument(
        "-o", "--output", type=Path, help="Destination SQL file (default: <dump>.postgresql.sql)"
    )
    parser.add_argument(
        "--schema",
        type=Path,
        default=Path(__file__).with_name("postgresql_schema.sql"),
        help="PostgreSQL schema file",
    )
    parser.add_argument("--force", action="store_true", help="Overwrite an existing output file")
    return parser.parse_args()


def default_output_path(source: Path) -> Path:
    name = source.name
    if name.endswith(".sql.gz"):
        name = name[:-7]
    elif name.endswith(".gz"):
        name = name[:-3]
    elif name.endswith(".sql"):
        name = name[:-4]
    return source.with_name(name + ".postgresql.sql")


def open_dump(path: Path) -> TextIO:
    with path.open("rb") as probe:
        compressed = probe.read(2) == b"\x1f\x8b"
    if compressed:
        return gzip.open(path, "rt", encoding="utf-8", errors="strict", newline="")
    return path.open("r", encoding="utf-8", errors="strict", newline="")


def iter_relevant_statements(stream: Iterable[str]) -> Iterator[str]:
    buffer: List[str] = []
    quoted = False
    escaped = False

    for line in stream:
        if not buffer and not (CREATE_START_RE.match(line) or INSERT_START_RE.match(line)):
            continue

        buffer.append(line)
        ended = False
        for character in line:
            if quoted:
                if escaped:
                    escaped = False
                elif character == "\\":
                    escaped = True
                elif character == "'":
                    quoted = False
            elif character == "'":
                quoted = True
            elif character == ";":
                ended = True
                break

        if ended:
            yield "".join(buffer)
            buffer = []
            quoted = False
            escaped = False

    if buffer:
        raise ConversionError("The dump ended in the middle of a CREATE TABLE or INSERT statement")


def parse_target_columns(schema_sql: str) -> Dict[str, Tuple[str, ...]]:
    result: Dict[str, Tuple[str, ...]] = {}
    for match in PG_TABLE_RE.finditer(schema_sql):
        table = match.group("table").lower()
        result[table] = tuple(column.group("column") for column in PG_COLUMN_RE.finditer(match.group("body")))
    missing = set(TABLE_ORDER) - set(result)
    if missing:
        raise ConversionError("Target schema does not define: " + ", ".join(sorted(missing)))
    return result


def parse_mysql_create(statement: str) -> Tuple[str, Tuple[str, ...]]:
    match = CREATE_TABLE_RE.match(statement)
    if not match:
        raise ConversionError("Unsupported CREATE TABLE statement")
    columns = tuple(column.group("column") for column in MYSQL_COLUMN_RE.finditer(match.group("body")))
    if not columns:
        raise ConversionError(f"No columns found in MariaDB table {match.group('table')}")
    return match.group("table"), columns


def parse_mysql_string(text: str, start: int) -> Tuple[str, int]:
    if text[start] != "'":
        raise AssertionError("string parser called at a non-string value")
    result: List[str] = []
    index = start + 1
    escapes = {
        "0": "\0",
        "b": "\b",
        "n": "\n",
        "r": "\r",
        "t": "\t",
        "Z": "\x1a",
        "\\": "\\",
        "'": "'",
        '"': '"',
    }
    while index < len(text):
        character = text[index]
        if character == "\\":
            index += 1
            if index >= len(text):
                raise ConversionError("Incomplete MariaDB string escape")
            escaped = text[index]
            result.append(escapes.get(escaped, escaped))
            index += 1
        elif character == "'":
            if index + 1 < len(text) and text[index + 1] == "'":
                result.append("'")
                index += 2
            else:
                return "".join(result), index + 1
        else:
            result.append(character)
            index += 1
    raise ConversionError("Unterminated MariaDB string literal")


def parse_rows(body: str) -> Iterator[List[Tuple[str, Optional[str]]]]:
    index = 0
    length = len(body)
    while True:
        while index < length and (body[index].isspace() or body[index] == ","):
            index += 1
        if index >= length:
            return
        if body[index] != "(":
            raise ConversionError(f"Expected '(' at offset {index} in INSERT values")
        index += 1
        row: List[Tuple[str, Optional[str]]] = []
        while True:
            while index < length and body[index].isspace():
                index += 1
            if index >= length:
                raise ConversionError("Unterminated row in INSERT values")
            if body[index] == "'":
                value, index = parse_mysql_string(body, index)
                row.append(("string", value))
            else:
                start = index
                while index < length and body[index] not in ",)":
                    index += 1
                token = body[start:index].strip()
                if token.upper() == "NULL":
                    row.append(("null", None))
                elif NUMBER_RE.fullmatch(token):
                    row.append(("number", token))
                else:
                    raise ConversionError(f"Unsupported unquoted MariaDB value: {token!r}")

            while index < length and body[index].isspace():
                index += 1
            if index >= length:
                raise ConversionError("Unterminated row in INSERT values")
            delimiter = body[index]
            index += 1
            if delimiter == ")":
                yield row
                break
            if delimiter != ",":
                raise ConversionError(f"Expected ',' or ')' at offset {index - 1}")


def pg_string(value: str) -> str:
    escaped = (
        value.replace("\\", "\\\\")
        .replace("'", "''")
        .replace("\0", "\\000")
        .replace("\x1a", "\\032")
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace("\t", "\\t")
        .replace("\b", "\\b")
    )
    return "E'" + escaped + "'"


def pg_value(table: str, column: str, value: Tuple[str, Optional[str]]) -> str:
    kind, raw = value
    if kind == "null":
        return "NULL"
    if (table, column) in BOOLEAN_COLUMNS:
        if kind == "number" and raw in {"0", "1"}:
            return "TRUE" if raw == "1" else "FALSE"
        raise ConversionError(f"Expected 0 or 1 for boolean {table}.{column}")
    if kind == "number":
        assert raw is not None
        return raw
    assert raw is not None
    return pg_string(raw)


def write_insert(
    destination: TextIO,
    statement: str,
    dump_columns: Dict[str, Tuple[str, ...]],
    target_columns: Dict[str, Tuple[str, ...]],
) -> Tuple[str, int]:
    match = INSERT_RE.match(statement)
    if not match:
        raise ConversionError("Unsupported INSERT statement")
    table = match.group("table")
    if table not in target_columns:
        if table in DEPRECATED_TABLES:
            return table, 0
        raise ConversionError(f"Dump contains unsupported table: {table}")

    explicit = match.group("columns")
    if explicit:
        columns = tuple(re.findall(r"`([^`]+)`", explicit))
    else:
        columns = dump_columns.get(table, ())
    if not columns:
        raise ConversionError(f"Column order for table {table} is unknown")
    unknown = set(columns) - set(target_columns[table])
    if unknown:
        raise ConversionError(f"Unsupported column(s) in {table}: {', '.join(sorted(unknown))}")

    destination.write(f"INSERT INTO {table} ({','.join(columns)}) VALUES\n")
    count = 0
    for row in parse_rows(match.group("body")):
        if len(row) != len(columns):
            raise ConversionError(
                f"Table {table}: row has {len(row)} values, expected {len(columns)}"
            )
        if count:
            destination.write(",\n")
        destination.write("(" + ",".join(pg_value(table, column, value) for column, value in zip(columns, row)) + ")")
        count += 1
    if not count:
        raise ConversionError(f"Empty INSERT statement for table {table}")
    destination.write(";\n\n")
    return table, count


def write_empty_guard(output: TextIO) -> None:
    checks = " OR\n       ".join(f"EXISTS (SELECT 1 FROM {table})" for table in TABLE_ORDER)
    output.write(
        "DO $migration$\nBEGIN\n"
        "    IF " + checks + " THEN\n"
        "        RAISE EXCEPTION 'Migration target is not empty';\n"
        "    END IF;\nEND\n$migration$;\n\n"
    )


def write_count_validation(output: TextIO, counts: Dict[str, int]) -> None:
    output.write("DO $validation$\nBEGIN\n")
    for table in TABLE_ORDER:
        output.write(
            f"    IF (SELECT COUNT(*) FROM {table}) <> {counts[table]} THEN\n"
            f"        RAISE EXCEPTION 'Imported row count mismatch for {table}';\n"
            "    END IF;\n"
        )
    output.write("END\n$validation$;\n\n")


def convert(source: Path, destination: Path, schema_path: Path, force: bool) -> Dict[str, int]:
    if not source.is_file():
        raise ConversionError(f"Dump not found: {source}")
    if not schema_path.is_file():
        raise ConversionError(f"PostgreSQL schema not found: {schema_path}")
    if destination.exists() and not force:
        raise ConversionError(f"Output already exists: {destination}; pass --force to overwrite it")

    schema_sql = schema_path.read_text(encoding="utf-8")
    target_columns = parse_target_columns(schema_sql)
    dump_columns: Dict[str, Tuple[str, ...]] = {}
    seen_tables = set()
    counts = {table: 0 for table in TABLE_ORDER}

    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="smoky-db-migration-") as temporary:
        temp_path = Path(temporary)
        table_files = {table: (temp_path / f"{table}.sql") for table in TABLE_ORDER}
        handles = {table: path.open("w", encoding="utf-8", newline="\n") for table, path in table_files.items()}
        try:
            with open_dump(source) as dump:
                for statement in iter_relevant_statements(dump):
                    create = CREATE_START_RE.match(statement)
                    if create:
                        table, columns = parse_mysql_create(statement)
                        seen_tables.add(table)
                        if table in target_columns:
                            dump_columns[table] = columns
                        elif table not in DEPRECATED_TABLES:
                            raise ConversionError(f"Dump contains unsupported table: {table}")
                        continue

                    table_name = INSERT_START_RE.match(statement).group("table")  # type: ignore[union-attr]
                    if table_name in DEPRECATED_TABLES:
                        continue
                    if table_name not in handles:
                        raise ConversionError(f"Dump contains unsupported table: {table_name}")
                    table, added = write_insert(handles[table_name], statement, dump_columns, target_columns)
                    counts[table] += added
        finally:
            for handle in handles.values():
                handle.close()

        missing = REQUIRED_SOURCE_TABLES - seen_tables
        if missing:
            raise ConversionError("Required source table(s) missing: " + ", ".join(sorted(missing)))

        with destination.open("w", encoding="utf-8", newline="\n") as output:
            output.write("\\set ON_ERROR_STOP on\n")
            output.write("-- Generated from a MariaDB dump by convert_mariadb_dump.py.\n")
            output.write("BEGIN;\nSET TIME ZONE 'UTC';\n")
            output.write("SELECT pg_advisory_xact_lock(hashtext('smoky_plugin_v2_dump_import'));\n\n")
            output.write(schema_sql.rstrip() + "\n\n")
            write_empty_guard(output)
            for table in TABLE_ORDER:
                if table_files[table].stat().st_size:
                    output.write(table_files[table].read_text(encoding="utf-8"))
            write_count_validation(output, counts)

            output.write(
                "INSERT INTO warning_sequences(server_id,next_id)\n"
                "SELECT id,1 FROM servers ON CONFLICT(server_id) DO NOTHING;\n"
                "UPDATE warning_sequences ws SET next_id=GREATEST(ws.next_id,\n"
                "    (SELECT COALESCE(MAX(w.id),0)+1 FROM warnings w WHERE w.server_id=ws.server_id));\n\n"
                "SELECT setval(pg_get_serial_sequence('servers','id'),COALESCE(MAX(id),1),EXISTS(SELECT 1 FROM servers)) FROM servers;\n"
                "SELECT setval(pg_get_serial_sequence('players','id'),COALESCE(MAX(id),1),EXISTS(SELECT 1 FROM players)) FROM players;\n"
                "INSERT INTO schema_migrations(version,description) VALUES\n"
                "    (5,'Referral program'),\n"
                "    (6,'PostgreSQL storage')\n"
                "ON CONFLICT(version) DO NOTHING;\n\n"
                "COMMIT;\nANALYZE;\n\n"
            )
            selects = [
                f"SELECT '{table}' AS table_name,COUNT(*) AS row_count FROM {table}"
                for table in TABLE_ORDER
            ]
            output.write("\nUNION ALL\n".join(selects) + "\nORDER BY table_name;\n")

    return counts


def main() -> int:
    args = parse_args()
    output = args.output or default_output_path(args.dump)
    try:
        counts = convert(args.dump.resolve(), output.resolve(), args.schema.resolve(), args.force)
    except (ConversionError, OSError, UnicodeError) as error:
        print(f"Migration conversion failed: {error}", file=sys.stderr)
        return 1

    print(f"PostgreSQL import script created: {output.resolve()}")
    for table in TABLE_ORDER:
        print(f"  {table}: {counts[table]} row(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
