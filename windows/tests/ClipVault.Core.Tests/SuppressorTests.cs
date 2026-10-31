using ClipVault.Core;

namespace ClipVault.Core.Tests;

// Ported from the #[cfg(test)] module in src-tauri/src/watcher.rs.
public class SuppressorTests
{
    [Fact]
    public void SuppressesOnlyTheMatchingText()
    {
        var s = new Suppressor();
        s.Expect("ours");
        Assert.False(s.ShouldSkip(Store.HashOf("someone else's")));
        s.Expect("ours");
        Assert.True(s.ShouldSkip(Store.HashOf("ours")));
    }

    [Fact]
    public void SuppressionIsConsumedOnce()
    {
        var s = new Suppressor();
        s.Expect("ours");
        Assert.True(s.ShouldSkip(Store.HashOf("ours")));
        Assert.False(s.ShouldSkip(Store.HashOf("ours")), "must not swallow a real re-copy");
    }

    [Fact]
    public void BlanketSuppressionSkipsAnyContent()
    {
        var s = new Suppressor();
        s.ExpectAny();
        Assert.True(s.ShouldSkip(Store.HashOf("a vault secret nobody predicted")));
    }

    [Fact]
    public void StaleSuppressionDoesNotEatALaterCopy()
    {
        var s = new Suppressor();
        s.ArmStaleForTest();
        Assert.False(s.ShouldSkip(Store.HashOf("unrelated copy")));
    }
}
