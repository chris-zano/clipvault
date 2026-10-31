using ClipVault.Core;
using ClipVault.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace ClipVault.Core.Tests;

// Ported from the #[cfg(test)] module in src-tauri/src/store.rs.
public sealed class StoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Store _store;

    public StoreTests()
    {
        _store = Store.Open(Path.Combine(_dir.Path, "t.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void RecordsAndReadsBack()
    {
        var id = _store.Record(new Clip.TextClip("hello world"))!.Value;
        Assert.Equal("hello world", _store.Content(id));
    }

    [Fact]
    public void IgnoresBlankCopies()
    {
        Assert.Null(_store.Record(new Clip.TextClip("   \n\t ")));
        Assert.Empty(_store.List("", 10));
    }

    [Fact]
    public void RecopyingMovesToTopInsteadOfDuplicating()
    {
        var first = _store.Record(new Clip.TextClip("repeated"))!.Value;
        _store.Record(new Clip.TextClip("other"));
        var again = _store.Record(new Clip.TextClip("repeated"))!.Value;

        Assert.Equal(first, again); // same text should reuse the row
        var rows = _store.List("", 10);
        Assert.Equal(2, rows.Count);
        Assert.Equal(first, rows[0].Id); // the re-copied entry comes back first
        Assert.Equal(1, rows[0].UseCount);
    }

    [Fact]
    public void SearchMatchesInsideWords()
    {
        _store.Record(new Clip.TextClip("https://example.com/p?token=AKIAIOSFODNN7EXAMPLE"));
        _store.Record(new Clip.TextClip("nothing to see"));

        // The substring is mid-token - a word tokenizer would miss this.
        Assert.Single(_store.List("AKIA", 10));
        Assert.Single(_store.List("token=", 10));
        Assert.Empty(_store.List("absent", 10));
    }

    [Fact]
    public void SearchTreatsWildcardsLiterally()
    {
        _store.Record(new Clip.TextClip("plain text"));
        _store.Record(new Clip.TextClip("50% off"));
        // '%' must not act as a LIKE wildcard matching everything.
        var hits = _store.List("%", 10);
        Assert.Single(hits);
        Assert.Contains("50%", hits[0].Preview);
    }

    [Fact]
    public void PinnedEntriesSortFirstAndSurviveClear()
    {
        var old = _store.Record(new Clip.TextClip("pin me"))!.Value;
        _store.Record(new Clip.TextClip("newer"));
        _store.SetPinned(old, true);

        Assert.Equal(old, _store.List("", 10)[0].Id);

        _store.Clear(includePinned: false);
        var left = _store.List("", 10);
        Assert.Single(left);
        Assert.Equal(old, left[0].Id);

        _store.Clear(includePinned: true);
        Assert.Empty(_store.List("", 10));
    }

    [Fact]
    public void TrimDropsOldestUnpinnedButKeepsPins()
    {
        var pinned = _store.Record(new Clip.TextClip("keep forever"))!.Value;
        _store.SetPinned(pinned, true);
        for (var i = 0; i < Store.MaxUnpinned + 25; i++)
        {
            _store.Record(new Clip.TextClip($"entry {i}"));
        }
        var all = _store.List("", 2000);
        Assert.Equal(Store.MaxUnpinned + 1, all.Count); // pin plus the capped unpinned set
        Assert.Contains(all, e => e.Id == pinned);
    }

    [Fact]
    public void RecordsAnImageWithDimensionsAndThumbnail()
    {
        var id = _store.Record(TestImages.MakeClip(120, 80, 5))!.Value;
        var row = _store.List("", 10).First(e => e.Id == id);

        Assert.Equal("image", row.Kind);
        Assert.Equal((120L, 80L), (row.Width, row.Height));
        Assert.Contains("120x80", row.Preview);
        Assert.StartsWith("data:image/png;base64,", row.Thumb);
        Assert.NotNull(_store.ImagePng(id));
    }

    [Fact]
    public void RecopyingAnImageDoesNotDuplicateIt()
    {
        var first = _store.Record(TestImages.MakeClip(60, 60, 9))!.Value;
        var again = _store.Record(TestImages.MakeClip(60, 60, 9))!.Value;
        Assert.Equal(first, again);
        Assert.Single(_store.List("", 10));
    }

    [Fact]
    public void ImagesAndTextCoexist()
    {
        _store.Record(new Clip.TextClip("some text"));
        _store.Record(TestImages.MakeClip(40, 40, 2));
        var rows = _store.List("", 10);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, e => e.Kind == "image");
    }

    [Fact]
    public void SearchingForImageFindsImageRowsOnly()
    {
        _store.Record(new Clip.TextClip("ordinary text"));
        _store.Record(TestImages.MakeClip(40, 40, 2));
        var hits = _store.List("Image", 10);
        Assert.Single(hits);
        Assert.Equal("image", hits[0].Kind);
    }

    [Fact]
    public void TextSearchDoesNotMatchImageBlobs()
    {
        _store.Record(TestImages.MakeClip(40, 40, 2));
        // Images store an empty content string; a search must not match them
        // just because the LIKE pattern is permissive.
        Assert.Empty(_store.List("ordinary", 10));
    }

    [Fact]
    public void OpensADatabaseWrittenBeforeImagesExisted()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "old.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE entries (
                     id INTEGER PRIMARY KEY, content TEXT NOT NULL, hash TEXT NOT NULL UNIQUE,
                     preview TEXT NOT NULL, truncated INTEGER NOT NULL DEFAULT 0,
                     bytes INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                     created_at INTEGER NOT NULL, last_used_at INTEGER NOT NULL,
                     use_count INTEGER NOT NULL DEFAULT 0);
                 INSERT INTO entries (content, hash, preview, bytes, created_at, last_used_at)
                 VALUES ('from the old schema', 'oldhash', 'from the old schema', 19, 1, 1);
                """;
            cmd.ExecuteNonQuery();
        }

        using var s = Store.Open(path);
        var rows = s.List("", 10);
        Assert.Single(rows); // the existing entry survives the migration
        Assert.Equal("text", rows[0].Kind); // rows written before images default to text

        // And the upgraded table takes images.
        s.Record(TestImages.MakeClip(30, 30, 1));
        Assert.Equal(2, s.List("", 10).Count);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "twice.db");
        using (var s1 = Store.Open(path))
        {
            s1.Record(new Clip.TextClip("hi"));
        }
        using var s2 = Store.Open(path);
        Assert.Single(s2.List("", 10));
    }

    /// <summary>Records an entry whose `bytes` is predictable, for budget tests.</summary>
    private static long Sized(Store s, string label, int bytes)
    {
        var text = label + new string('x', Math.Max(0, bytes - label.Length));
        return s.Record(new Clip.TextClip(text))!.Value;
    }

    [Fact]
    public void ByteBudgetDropsOldestUnpinned()
    {
        var oldest = Sized(_store, "a", 400);
        var middle = Sized(_store, "b", 400);
        var newest = Sized(_store, "c", 400);

        // Budget counts the bytes ahead of each entry: the newest sees 0, the
        // middle 400, the oldest 800. A 700 budget therefore keeps two.
        _store.TrimTo(1000, 700);

        var left = _store.List("", 10).Select(e => e.Id).ToList();
        Assert.Contains(newest, left);
        Assert.Contains(middle, left);
        Assert.DoesNotContain(oldest, left); // the oldest should go first
    }

    [Fact]
    public void NewestEntrySurvivesEvenIfItAloneExceedsTheBudget()
    {
        var big = Sized(_store, "big", 5000);
        _store.TrimTo(1000, 100);
        var left = _store.List("", 10);
        Assert.Single(left);
        Assert.Equal(big, left[0].Id); // copying something must not immediately lose it
    }

    [Fact]
    public void PinsAreExemptFromTheByteBudget()
    {
        var pinned = Sized(_store, "keep", 5000);
        _store.SetPinned(pinned, true);
        for (var i = 0; i < 5; i++)
        {
            Sized(_store, $"junk{i}", 2000);
        }
        _store.TrimTo(1000, 1000);

        var left = _store.List("", 20);
        Assert.Contains(left, e => e.Id == pinned); // a pin outlives the budget
    }

    [Fact]
    public void BudgetAndCountLimitsBothApply()
    {
        for (var i = 0; i < 20; i++)
        {
            Sized(_store, $"e{i}", 100);
        }
        // Count allows 5, budget allows about 3.
        _store.TrimTo(5, 300);
        Assert.True(_store.List("", 50).Count <= 3);
    }

    [Fact]
    public void PreviewIsTruncatedAndFlagged()
    {
        const int previewChars = 220;
        var longText = new string('x', previewChars + 50);
        var id = _store.Record(new Clip.TextClip(longText))!.Value;
        var row = _store.List("", 10).First(e => e.Id == id);
        Assert.True(row.Truncated);
        Assert.Equal(previewChars, row.Preview.Length);
        // The full text is still retrievable even though the preview is cut.
        Assert.Equal(longText.Length, _store.Content(id)!.Length);
    }
}
