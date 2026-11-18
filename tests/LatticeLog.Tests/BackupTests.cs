using LatticeLog.Storage;

namespace LatticeLog.Tests;

/// <summary>Hot backup (P24): consistent while the writer keeps appending; the copy recovers like a crash image.</summary>
public class BackupTests
{
    private static List<LogRecord> Batch(int start, int n)
        => Enumerable.Range(start, n).Select(i => new LogRecord
        {
            Severity = Severity.Info, EventName = "tick", Attributes = [new("i", i)], TimestampUnixNanos = 1_800_000_000_000_000_000L + i,
        }).ToList();

    [Fact]
    public void Backup_while_writing_is_a_verified_prefix_and_restores_into_a_writable_store()
    {
        var root = TestData.TempDir();
        var dest = Path.Combine(TestData.TempDir(), "bk");
        using (var w = StoreWriter.Open(root, new StoreWriterOptions { MinFreeDiskBytes = 0, SegmentMaxBytes = 16 * 1024 }))
        {
            for (var b = 0; b < 30; b++) w.WriteBatch(Batch(b * 100, 100));
            w.Flush(AckLevel.FsyncConfirmed);
            var acknowledged = 3_000;
            var result = StoreBackup.Create(root, dest); // writer still open, active segment unsealed
            Assert.True(result.Verified, string.Join("; ", result.Issues));
            Assert.True(result.Segments >= 2);
            Assert.True(result.Records >= acknowledged); // everything acknowledged before the backup is in it
            w.WriteBatch(Batch(3_000, 100)); // the source keeps going
        }
        Assert.Throws<IOException>(() => StoreBackup.Create(root, dest)); // never overwrites a backup

        // Restore = open the copy as a store: recovery seals the partial active segment and writing continues.
        using (var w = StoreWriter.Open(dest, new StoreWriterOptions { MinFreeDiskBytes = 0 }))
            w.WriteBatch(Batch(9_000, 10));
        var restored = StoreReader.Open(dest).ReadAll(new ScanReport()).Select(r => r.Attributes[0].Value.AsInt64()).ToList();
        Assert.Equal(Enumerable.Range(0, 3_000).Select(i => (long)i).Concat(Enumerable.Range(9_000, 10).Select(i => (long)i)), restored);
        var seqs = StoreReader.Open(dest).ReadAll(new ScanReport()).Select(r => r.Sequence).ToList();
        Assert.Equal(seqs.OrderBy(s => s).Distinct(), seqs);
    }
}
