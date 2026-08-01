using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoaChess.DataGen;

// Writes a dataset as a sequence of independently valid shards (v4.1.0).
//
// WHY. A NOADATA file only becomes usable when its header is patched with the
// final record count and manifest hash, which happens after the last game. That
// is fine for a 13-hour run and unacceptable for the multi-day runs BLOCK 12's
// 300-500M position target requires: a crash, a power cut or an accidental
// Ctrl-C at hour 40 destroys everything written so far. The pipeline even
// checks for this ("the datagen did not reach 'done:' -> the file is useless").
//
// Sharding removes that cliff. Every `--shard-size` records the current shard is
// closed properly — header patched, manifest written, SHA recorded — and a new
// one begins. An interrupted run therefore loses at most the shard in flight,
// and every completed shard is a fully valid dataset that training can consume
// immediately (the streaming FeatureStore already takes many files).
//
// It also makes the work RESUMABLE: --resume counts the shards already on disk
// and continues numbering after them, so a long campaign can be run in sessions
// around the machine's other commitments instead of in one uninterruptible block.
public sealed class ShardWriter : IDisposable
{
    private readonly string _basePath;
    private readonly long _shardSize;
    // (shardIndex, recordsInShard, datasetSha) -> manifest object to serialise.
    private readonly Func<int, long, string, object> _manifestFactory;

    private FileStream? _stream;
    private string _currentPath = "";
    private long _recordsInShard;
    private int _shardIndex;

    public long TotalRecords { get; private set; }
    public int CompletedShards { get; private set; }

    // basePath "data/x.noadata" produces "data/x.0000.noadata", "x.0001.noadata"...
    // A shardSize of 0 means "one shard", which keeps the classic single-file
    // layout for small runs (and for every existing script that expects it).
    public ShardWriter(string basePath, long shardSize, int startIndex,
                       Func<int, long, string, object> manifestFactory)
    {
        _basePath = basePath;
        _shardSize = shardSize;
        _shardIndex = startIndex;
        _manifestFactory = manifestFactory;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(basePath))!);

        // A resumed run starts its position count from what is ALREADY on disk,
        // so --positions means the size of the finished corpus rather than the
        // size of this session. Without this, resuming a 20M-position target
        // that stopped at 12M would produce 32M, and every interruption would
        // silently inflate the corpus past what was asked for.
        TotalRecords = CountExistingRecords(basePath, startIndex);
        if (TotalRecords > 0)
            Console.WriteLine($"resume : {TotalRecords:N0} positions already on disk; "
                            + "the --positions target counts these");

        OpenShard();
    }

    // Sums the header record counts of shards [0, upToIndex). Only finalized
    // shards are counted, which is the same set CountCompletedShards reports.
    public static long CountExistingRecords(string basePath, int upToIndex)
    {
        long total = 0;
        // Allocated ONCE, outside the loop. A stackalloc per iteration grows the
        // frame with the shard count and never releases until the method
        // returns (CA2014) — with the 60+ shards a 300M-position corpus
        // produces, that is a stack overflow waiting for a big enough campaign.
        Span<byte> header = stackalloc byte[DatasetFormat.HeaderSize];
        for (int index = 0; index < upToIndex; index++)
        {
            string path = ShardPath(basePath, index);
            if (!File.Exists(path))
                continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite);
                if (stream.Read(header) == DatasetFormat.HeaderSize)
                    total += (long)System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt64LittleEndian(header[24..]);
            }
            catch (IOException)
            {
                // Unreadable shard: leave it out of the count rather than fail.
                // Overshooting the target slightly is harmless; refusing to
                // resume at all is not.
            }
        }
        return total;
    }

    private static string ShardPath(string basePath, int index) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(basePath))!,
            Path.GetFileNameWithoutExtension(basePath) + "." + index.ToString("D4")
                + Path.GetExtension(basePath));

    public bool IsSharded => _shardSize > 0;

    public string PathForShard(int index) =>
        _shardSize <= 0 ? _basePath : ShardPath(_basePath, index);

    // Counts the shards already present so a resumed run continues numbering
    // instead of overwriting finished work. Only shards with a manifest count:
    // a manifest is written last, so its absence marks an interrupted shard.
    public static int CountCompletedShards(string basePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(basePath))!;
        if (!Directory.Exists(dir))
            return 0;
        string stem = Path.GetFileNameWithoutExtension(basePath);
        string extension = Path.GetExtension(basePath);
        int index = 0;
        while (File.Exists(Path.Combine(dir, $"{stem}.{index:D4}{extension}.manifest.json")))
            index++;
        return index;
    }

    private void OpenShard()
    {
        _currentPath = PathForShard(_shardIndex);
        _stream = new FileStream(_currentPath, FileMode.Create, FileAccess.Write);
        DatasetFormat.WriteHeader(_stream, 0, stackalloc byte[32]);
        _recordsInShard = 0;
    }

    // Appends one record. Caller must hold whatever lock protects concurrency;
    // the writer itself is deliberately not synchronised, because the datagen
    // already batches records under a single lock and adding a second one would
    // only obscure that.
    public void Write(ReadOnlySpan<byte> record)
    {
        _stream!.Write(record);
        _recordsInShard++;
        TotalRecords++;
    }

    // Closes the current shard if it has reached the target size. Called between
    // games rather than between records, so a game's positions are never split
    // across two shards — the format's records are ordered by game and the
    // training split relies on that.
    public void RollIfNeeded()
    {
        if (_shardSize > 0 && _recordsInShard >= _shardSize)
        {
            FinalizeShard();
            _shardIndex++;
            OpenShard();
        }
    }

    private void FinalizeShard()
    {
        if (_stream is null)
            return;
        _stream.Dispose();
        _stream = null;

        if (_recordsInShard == 0)
        {
            // Nothing was written; leave no empty file behind to confuse a
            // later resume or a corpus scan.
            File.Delete(_currentPath);
            return;
        }

        string datasetSha;
        using (var read = File.OpenRead(_currentPath))
            datasetSha = Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant();

        object manifest = _manifestFactory(_shardIndex, _recordsInShard, datasetSha);
        string json = JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true });

        // The header is patched BEFORE the manifest is written: the manifest is
        // the completion marker CountCompletedShards looks for, so it must never
        // exist next to a shard whose header still says zero records.
        byte[] manifestSha = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        using (var patch = new FileStream(_currentPath, FileMode.Open, FileAccess.Write))
            DatasetFormat.WriteHeader(patch, (ulong)_recordsInShard, manifestSha);

        File.WriteAllText(_currentPath + ".manifest.json", json);

        CompletedShards++;
        Console.WriteLine($"  shard {_shardIndex:D4} closed: {_recordsInShard:N0} records -> {_currentPath}");
    }

    public void Dispose() => FinalizeShard();
}
