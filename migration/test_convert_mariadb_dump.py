import gzip
import tempfile
import unittest
from pathlib import Path

from convert_mariadb_dump import ConversionError, convert


SCHEMA = Path(__file__).with_name("postgresql_schema.sql")


class DumpConversionTests(unittest.TestCase):
    def test_converts_gzip_dump_and_preserves_explicit_ids(self):
        dump = """\
CREATE TABLE `servers` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `display_name` varchar(128) NOT NULL,
  `game_port` smallint unsigned NOT NULL,
  `created_at` datetime(6) NOT NULL,
  `updated_at` datetime(6) NOT NULL
) ENGINE=InnoDB;
INSERT INTO `servers` VALUES
(7,'Test server',7777,'2026-07-26 00:00:00.000001','2026-07-26 00:00:00.000002');
CREATE TABLE `players` (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `steam_id` varchar(32) NOT NULL,
  `last_nickname` varchar(64),
  `statistics_private` tinyint(1) NOT NULL,
  `created_at` datetime(6) NOT NULL,
  `updated_at` datetime(6) NOT NULL
) ENGINE=InnoDB;
INSERT INTO `players` VALUES
(42,'76561198000000000','O\\'Brien\\nLine',1,'2026-07-26 00:00:00.000003','2026-07-26 00:00:00.000004');
"""
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "dump.sql.gz"
            output = Path(temporary) / "output.sql"
            with gzip.open(source, "wt", encoding="utf-8") as stream:
                stream.write(dump)

            counts = convert(source, output, SCHEMA, force=False)
            generated = output.read_text(encoding="utf-8")

        self.assertEqual(1, counts["servers"])
        self.assertEqual(1, counts["players"])
        self.assertIn("INSERT INTO players (id,steam_id,last_nickname,statistics_private,created_at,updated_at)", generated)
        self.assertIn("(42,E'76561198000000000',E'O''Brien\\nLine',TRUE", generated)
        self.assertIn("setval(pg_get_serial_sequence('players','id')", generated)
        self.assertIn("Migration target is not empty", generated)
        self.assertIn("Imported row count mismatch for players", generated)

    def test_rejects_unknown_tables_instead_of_silently_dropping_data(self):
        dump = """\
CREATE TABLE `servers` (`id` bigint NOT NULL) ENGINE=InnoDB;
CREATE TABLE `players` (`id` bigint NOT NULL) ENGINE=InnoDB;
CREATE TABLE `unexpected_data` (`id` bigint NOT NULL) ENGINE=InnoDB;
"""
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "dump.sql"
            output = Path(temporary) / "output.sql"
            source.write_text(dump, encoding="utf-8")
            with self.assertRaisesRegex(ConversionError, "unsupported table"):
                convert(source, output, SCHEMA, force=False)


if __name__ == "__main__":
    unittest.main()
