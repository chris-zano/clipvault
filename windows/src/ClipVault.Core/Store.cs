using Microsoft.Data.Sqlite;

namespace ClipVault.Core;

/// <summary>
/// SQLite-backed clipboard history and vault storage. A direct port of
/// <c>store.rs</c>: same schema, same SQL, same limits, so a database
/// written by either build opens cleanly in the other.
/// </summary>
public sealed class Store : IDisposable
{
    /// <summary>Unpinned entries beyond this count are dropped, oldest first. Pins never expire.</summary>
    public const int MaxUnpinned = 500;

    /// <summary>
    /// Unpinned entries are also held to a byte budget, because a few dozen
    /// screenshots outweigh thousands of text clips and the count alone will
    /// not notice. Pins are exempt from both limits: keeping them is a
    /// deliberate act.
    /// </summary>
    public const long MaxUnpinnedBytes = 256L * 1024 * 1024;

    private const int PreviewChars = 220;

    private readonly SqliteConnection _conn;

    private Store(SqliteConnection conn)
    {
        _conn = conn;
    }

    public static Store Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        // PRAGMA journal_mode returns the resulting mode as a row, so it needs
        // a scalar/reader call; ExecuteNonQuery alone can leave it unapplied.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteScalar();
        }
        Exec(conn, "PRAGMA synchronous = NORMAL;");

        var store = new Store(conn);
        store.Migrate();
        return store;
    }

    public void Dispose() => _conn.Dispose();

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string HashOf(string text) => Hashing.Sha256Hex(text);

    private static (string Preview, bool Truncated) PreviewOf(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var src = collapsed.Length == 0 ? text.Trim() : collapsed;

        var enumerator = StringInfoEnumerator(src);
        var chars = new List<string>();
        foreach (var grapheme in enumerator)
        {
            if (chars.Count >= PreviewChars)
            {
                return (string.Concat(chars), true);
            }
            chars.Add(grapheme);
        }
        return (string.Concat(chars), false);
    }

    // .NET strings are UTF-16; Rust counts Unicode scalar values (chars).
    // Enumerating by rune keeps the truncation point comparable to the Rust
    // version for any text that is not itself made of surrogate pairs.
    private static IEnumerable<string> StringInfoEnumerator(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var len = char.IsSurrogatePair(s, i) ? 2 : 1;
            yield return s.Substring(i, len);
            i += len;
        }
    }

    private void Migrate()
    {
        Exec(_conn, """
            CREATE TABLE IF NOT EXISTS entries (
                id           INTEGER PRIMARY KEY,
                content      TEXT    NOT NULL,
                hash         TEXT    NOT NULL UNIQUE,
                preview      TEXT    NOT NULL,
                truncated    INTEGER NOT NULL DEFAULT 0,
                bytes        INTEGER NOT NULL,
                pinned       INTEGER NOT NULL DEFAULT 0,
                created_at   INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL,
                use_count    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_entries_recent
                ON entries (pinned DESC, last_used_at DESC);

            CREATE TABLE IF NOT EXISTS vault_meta (
                id            INTEGER PRIMARY KEY CHECK (id = 1),
                salt          BLOB NOT NULL,
                verify_nonce  BLOB NOT NULL,
                verify_ct     BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS vault_entries (
                id         INTEGER PRIMARY KEY,
                label      TEXT NOT NULL,
                nonce      BLOB NOT NULL,
                ciphertext BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );
            """);
        AddImageColumns();
    }

    /// <summary>
    /// Images arrived after the first release, so the columns are added to
    /// whatever table is already on disk rather than assumed present.
    /// </summary>
    private void AddImageColumns()
    {
        var existing = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('entries')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach (var (name, decl) in new[]
        {
            ("kind", "TEXT NOT NULL DEFAULT 'text'"),
            ("blob", "BLOB"),
            ("thumb", "BLOB"),
            ("width", "INTEGER"),
            ("height", "INTEGER"),
        })
        {
            if (!existing.Contains(name))
            {
                Exec(_conn, $"ALTER TABLE entries ADD COLUMN {name} {decl}");
            }
        }
    }

    // ---- history ---------------------------------------------------------

    /// <summary>Returns the row id, or null when there was nothing worth keeping.</summary>
    public long? Record(Clip clip)
    {
        if (clip is Clip.TextClip { Text: var t } && string.IsNullOrWhiteSpace(t))
        {
            return null;
        }

        var hash = clip.Hash();
        var ts = Now();

        // Re-copying something already in history moves it back to the top
        // rather than creating a duplicate row.
        long? existing;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM entries WHERE hash = $hash";
            cmd.Parameters.AddWithValue("$hash", hash);
            existing = (long?)cmd.ExecuteScalar();
        }

        if (existing is { } id)
        {
            using var upd = _conn.CreateCommand();
            upd.CommandText = "UPDATE entries SET last_used_at = $ts, use_count = use_count + 1 WHERE id = $id";
            upd.Parameters.AddWithValue("$ts", ts);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
            return id;
        }

        using (var ins = _conn.CreateCommand())
        {
            if (clip is Clip.TextClip text)
            {
                var (preview, truncated) = PreviewOf(text.Text);
                ins.CommandText = """
                    INSERT INTO entries
                       (kind, content, hash, preview, truncated, bytes, created_at, last_used_at)
                     VALUES ('text', $content, $hash, $preview, $truncated, $bytes, $ts, $ts)
                    """;
                ins.Parameters.AddWithValue("$content", text.Text);
                ins.Parameters.AddWithValue("$hash", hash);
                ins.Parameters.AddWithValue("$preview", preview);
                ins.Parameters.AddWithValue("$truncated", truncated ? 1 : 0);
                ins.Parameters.AddWithValue("$bytes", (long)System.Text.Encoding.UTF8.GetByteCount(text.Text));
                ins.Parameters.AddWithValue("$ts", ts);
            }
            else if (clip is Clip.ImageClip image)
            {
                var img = image.Image;
                var preview = $"Image {img.Width}x{img.Height} - {Hashing.HumanSize(img.Png.Length)}";
                ins.CommandText = """
                    INSERT INTO entries
                       (kind, content, hash, preview, truncated, bytes, created_at, last_used_at,
                        blob, thumb, width, height)
                     VALUES ('image', '', $hash, $preview, 0, $bytes, $ts, $ts, $blob, $thumb, $width, $height)
                    """;
                ins.Parameters.AddWithValue("$hash", hash);
                ins.Parameters.AddWithValue("$preview", preview);
                ins.Parameters.AddWithValue("$bytes", (long)img.Png.Length);
                ins.Parameters.AddWithValue("$ts", ts);
                ins.Parameters.AddWithValue("$blob", img.Png);
                ins.Parameters.AddWithValue("$thumb", img.ThumbPng);
                ins.Parameters.AddWithValue("$width", (long)img.Width);
                ins.Parameters.AddWithValue("$height", (long)img.Height);
            }
            else
            {
                throw new InvalidOperationException("unreachable");
            }
            ins.ExecuteNonQuery();
        }

        var newId = LastInsertRowId();
        Trim();
        return newId;
    }

    private void Trim() => TrimTo(MaxUnpinned, MaxUnpinnedBytes);

    internal void TrimTo(int maxUnpinned, long maxBytes)
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM entries WHERE pinned = 0 AND id NOT IN (
                     SELECT id FROM entries WHERE pinned = 0
                     ORDER BY last_used_at DESC LIMIT $maxUnpinned
                 )
                """;
            cmd.Parameters.AddWithValue("$maxUnpinned", (long)maxUnpinned);
            cmd.ExecuteNonQuery();
        }

        // Walk newest to oldest and drop everything past the budget. The sum
        // excludes the row itself, so the most recent entry always survives
        // even if it alone is larger than the budget - having just copied
        // something and finding it already gone would be worse than briefly
        // overshooting.
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM entries WHERE id IN (
                     SELECT id FROM (
                         SELECT id, SUM(bytes) OVER (
                             ORDER BY last_used_at DESC, id DESC
                             ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
                         ) AS preceding_bytes
                         FROM entries WHERE pinned = 0
                     ) WHERE preceding_bytes >= $maxBytes
                 )
                """;
            cmd.Parameters.AddWithValue("$maxBytes", maxBytes);
            cmd.ExecuteNonQuery();
        }
    }

    public List<Entry> List(string query, long limit)
    {
        var q = query.Trim();
        using var cmd = _conn.CreateCommand();

        // Substring match, not full-text: clipboard searches are things like
        // "AKIA" inside a key or a fragment of a URL, which a word tokenizer
        // would never match.
        if (q.Length == 0)
        {
            cmd.CommandText = """
                SELECT id, kind, preview, truncated, bytes, pinned, created_at, last_used_at,
                        use_count, width, height, thumb
                 FROM entries
                 ORDER BY pinned DESC, last_used_at DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, kind, preview, truncated, bytes, pinned, created_at, last_used_at,
                        use_count, width, height, thumb
                 FROM entries
                 WHERE (content LIKE $like ESCAPE '\' OR (kind = 'image' AND preview LIKE $like ESCAPE '\'))
                 ORDER BY pinned DESC, last_used_at DESC LIMIT $limit
                """;
            var escaped = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$like", $"%{escaped}%");
            cmd.Parameters.AddWithValue("$limit", limit);
        }

        var rows = new List<Entry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            byte[]? thumbBytes = reader.IsDBNull(11) ? null : (byte[])reader["thumb"];
            rows.Add(new Entry(
                Id: reader.GetInt64(0),
                Kind: reader.GetString(1),
                Preview: reader.GetString(2),
                Truncated: reader.GetInt64(3) != 0,
                Bytes: reader.GetInt64(4),
                Pinned: reader.GetInt64(5) != 0,
                CreatedAt: reader.GetInt64(6),
                LastUsedAt: reader.GetInt64(7),
                UseCount: reader.GetInt64(8),
                Width: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                Height: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                Thumb: thumbBytes is null ? null : $"data:image/png;base64,{Convert.ToBase64String(thumbBytes)}"));
        }
        return rows;
    }

    public string? Content(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (string?)cmd.ExecuteScalar();
    }

    public byte[]? ImagePng(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT blob FROM entries WHERE id = $id AND kind = 'image'";
        cmd.Parameters.AddWithValue("$id", id);
        var result = cmd.ExecuteScalar();
        return result is byte[] bytes ? bytes : null;
    }

    public string? Kind(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT kind FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (string?)cmd.ExecuteScalar();
    }

    public void MarkUsed(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET last_used_at = $ts, use_count = use_count + 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetPinned(long id, bool pinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE entries SET pinned = $pinned WHERE id = $id";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clears history. Pinned entries survive unless <paramref name="includePinned"/>.</summary>
    public void Clear(bool includePinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = includePinned ? "DELETE FROM entries" : "DELETE FROM entries WHERE pinned = 0";
        cmd.ExecuteNonQuery();
    }

    // ---- vault -------------------------------------------------------

    public (byte[] Salt, byte[] VerifyNonce, byte[] VerifyCt)? VaultMeta()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT salt, verify_nonce, verify_ct FROM vault_meta WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return ((byte[])reader["salt"], (byte[])reader["verify_nonce"], (byte[])reader["verify_ct"]);
    }

    public void SetVaultMeta(byte[] salt, byte[] nonce, byte[] ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vault_meta (id, salt, verify_nonce, verify_ct)
             VALUES (1, $salt, $nonce, $ct)
            """;
        cmd.Parameters.AddWithValue("$salt", salt);
        cmd.Parameters.AddWithValue("$nonce", nonce);
        cmd.Parameters.AddWithValue("$ct", ct);
        cmd.ExecuteNonQuery();
    }

    public List<VaultItem> VaultList()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, label, created_at FROM vault_entries ORDER BY label COLLATE NOCASE";
        var rows = new List<VaultItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new VaultItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return rows;
    }

    public long VaultInsert(string label, byte[] nonce, byte[] ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vault_entries (label, nonce, ciphertext, created_at)
             VALUES ($label, $nonce, $ct, $ts)
            """;
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$nonce", nonce);
        cmd.Parameters.AddWithValue("$ct", ct);
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.ExecuteNonQuery();
        return LastInsertRowId();
    }

    public (byte[] Nonce, byte[] Ciphertext)? VaultGet(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT nonce, ciphertext FROM vault_entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return ((byte[])reader["nonce"], (byte[])reader["ciphertext"]);
    }

    public void VaultDelete(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vault_entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private long LastInsertRowId()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Runs arbitrary SQL against the open connection. For tests only.</summary>
    internal void ExecuteRaw(string sql) => Exec(_conn, sql);
}
