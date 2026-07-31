# MariaDB to PostgreSQL migration

The converter accepts a regular `mariadb-dump` file in `.sql` or `.sql.gz`
format and creates a PostgreSQL script. Existing IDs, timestamps, Steam IDs,
Discord IDs, statistics, referrals, warnings and migration history are retained.

`postgresql_schema.sql` is the version 6 import schema, not the current runtime
baseline. It intentionally creates the legacy `warnings` tables so the imported
rows keep their original shape. On the first plugin startup, migration 7 copies
those warnings into the unified `punishments` table and removes the staging
tables in the same transaction.

The generated script intentionally aborts when the PostgreSQL target contains
any SmokyPluginV2 rows. It also rejects unknown tables or columns instead of
silently discarding data, verifies imported row counts inside the transaction,
repairs player/server identity sequences and updates warning sequences.

## Final cutover

1. Stop every SCP:SL instance that writes to the shared MariaDB database.
2. Create a fresh dump:

   ```bash
   mariadb-dump \
     --single-transaction \
     --quick \
     --default-character-set=utf8mb4 \
     --host=MARIADB_HOST \
     --user=MARIADB_USER \
     --password \
     MARIADB_DATABASE | gzip > smoky-final.sql.gz
   ```

3. Convert it on a machine with Python 3.8 or newer:

   ```bash
   python3 convert_mariadb_dump.py smoky-final.sql.gz \
     --output smoky-final.postgresql.sql
   ```

4. Import it into a new, empty PostgreSQL database:

   ```bash
   psql \
     --host=POSTGRES_HOST \
     --username=smoky_plugin_v2 \
     --dbname=smoky_plugin_v2 \
     --set=ON_ERROR_STOP=1 \
     --file=smoky-final.postgresql.sql
   ```

5. Check the row-count table printed by `psql`. The converter also prints the
   source counts when creating the script; they must match, apart from the two
   PostgreSQL migration records added after validation.
6. Update the shared `database.yml` to port `5432` and the PostgreSQL
   credentials. Deploy `SmokyPluginV2.dll`, `Npgsql.dll` and all nine Npgsql
   framework dependencies from `bin/Release`.
7. Start one game server, check that PostgreSQL initialization succeeds, then
   start the remaining instances.

Keep the stopped MariaDB database and the final dump unchanged until the new
installation has been verified. Do not start the MariaDB and PostgreSQL plugin
versions at the same time: they do not replicate changes between each other.

## Re-running conversion

The converter refuses to overwrite an existing output file. Use `--force` only
when intentionally regenerating it:

```bash
python3 convert_mariadb_dump.py smoky-final.sql.gz \
  --output smoky-final.postgresql.sql \
  --force
```

The PostgreSQL import itself is not rerunnable against a populated database by
design. Create a new empty database (or explicitly recreate the migration
database) before retrying a failed cutover.
