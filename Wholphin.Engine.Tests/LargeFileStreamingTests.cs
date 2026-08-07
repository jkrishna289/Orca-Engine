using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>
/// Phase 0 verification for torrent streaming: proves MonoTorrent can open a seekable stream over a
/// file well past the 2 GB boundary, and that seeking to an arbitrary offset returns the right bytes.
///
/// This exists because of MonoTorrent issue #326, where StreamProvider.CreateStreamAsync threw
/// ArgumentOutOfRangeException for files larger than 2 GB. Feature films are routinely 4-8 GB, so if
/// that regressed the whole design is dead — better to find out here than after the UI is built.
///
/// Seeds and leeches over loopback only: no trackers, no DHT, no peer traffic leaves the machine.
///
/// Skipped by default — it writes and hashes a multi-GB file, which takes minutes. Run explicitly:
///   dotnet test --filter "FullyQualifiedName~LargeFileStreamingTests"
/// after removing the Skip, or set ORCA_RUN_LARGE_STREAM_TEST=1.
/// </summary>
public class LargeFileStreamingTests
{
    // 3 GB: past the 2 GB boundary that issue #326 was about, while keeping the write + two hash
    // passes (torrent creation, then the seeder's verify) inside a sane runtime.
    private const long FileSize = 3L * 1024 * 1024 * 1024;
    private const int PieceLength = 4 * 1024 * 1024;
    private const int SeedPort = 45877;
    private const int LeechPort = 45878;

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("ORCA_RUN_LARGE_STREAM_TEST") == "1";

    [Fact]
    public async Task StreamsAndSeeks_InAFileLargerThan2Gb()
    {
        // Opt-in rather than a Skip attribute, which would mean taking on Xunit.SkippableFact just
        // to print a nicer word in the runner output.
        if (!Enabled)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "orca-stream-test-" + Guid.NewGuid().ToString("N")[..8]);
        var seedDir = Path.Combine(root, "seed");
        var leechDir = Path.Combine(root, "leech");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(leechDir);

        var mediaPath = Path.Combine(seedDir, "Big.Buck.Test.2024.1080p.mkv");

        try
        {
            WriteDeterministicFile(mediaPath, FileSize);

            // Build a torrent for the file, then seed it and stream it from two separate engines.
            var creator = new TorrentCreator(TorrentType.V1Only, Factories.Default)
            {
                PieceLength = PieceLength,
            };

            var metadata = await creator.CreateAsync(new TorrentFileSource(mediaPath), CancellationToken.None);
            var torrent = Torrent.Load(metadata);

            Assert.Equal(FileSize, torrent.Files.Single().Length);

            // Fixed ports rather than ephemeral: the engine reports the configured endpoint, not the
            // bound one, so port 0 would leave nothing to hand AddPeerAsync.
            using var seeder = new ClientEngine(LoopbackSettings(Path.Combine(root, "seed-cache"), SeedPort));
            using var leecher = new ClientEngine(LoopbackSettings(Path.Combine(root, "leech-cache"), LeechPort));

            var seedManager = await seeder.AddAsync(torrent, seedDir);
            await seedManager.StartAsync();

            // The seeder hash-checks the whole file before it can serve a byte. That has to finish
            // before the leecher asks for anything, or the prebuffer just waits on a peer that is
            // still busy — which is exactly how the first run of this test "failed".
            await WaitForAsync(
                () => seedManager.State == TorrentState.Seeding,
                TimeSpan.FromMinutes(10),
                () => $"seeder never reached Seeding (state={seedManager.State}, progress={seedManager.Progress:F1}%)");

            var leechManager = await leecher.AddStreamingAsync(torrent, leechDir);
            await leechManager.StartAsync();

            // Point the leecher straight at the seeder; without a tracker they would never meet.
            await leechManager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{SeedPort}")));

            await WaitForAsync(
                () => leechManager.Peers.Available > 0 || leechManager.Peers.Seeds > 0,
                TimeSpan.FromMinutes(2),
                () => "leecher never connected to the loopback seeder");

            var file = leechManager.Files.Single();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            // The call that threw ArgumentOutOfRangeException for >2 GB files in issue #326.
            await using var stream = await leechManager.StreamProvider!.CreateStreamAsync(file, true, cts.Token);

            Assert.Equal(FileSize, stream.Length);

            // Seek past the 2 GB boundary and confirm the bytes are the ones we wrote — this is what
            // a viewer scrubbing into the middle of a film actually does. Must land inside the file:
            // seeking to exactly FileSize is EOF and reads zero bytes.
            const long SeekOffset = 2560L * 1024 * 1024; // 2.5 GB
            Assert.True(SeekOffset > 2L * 1024 * 1024 * 1024, "seek must cross the 2 GB boundary to be meaningful");
            Assert.True(SeekOffset + 4096 < FileSize, "seek must land inside the file");

            stream.Seek(SeekOffset, SeekOrigin.Begin);

            var buffer = new byte[4096];
            var read = await ReadFullyAsync(stream, buffer, cts.Token);

            Assert.Equal(buffer.Length, read);
            for (var i = 0; i < buffer.Length; i++)
            {
                Assert.Equal(ExpectedByteAt(SeekOffset + i), buffer[i]);
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // Polls a condition and fails with what the state actually was, so a timeout says which stage
    // stalled instead of just "a task was canceled".
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, Func<string> describeFailure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail(describeFailure());
    }

    private static EngineSettings LoopbackSettings(string cacheDir, int port) =>
        new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            // Loopback only, no port forwarding, no LPD, no DHT — this test must not generate a
            // single packet that leaves the machine.
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            ListenEndPoints = new System.Collections.Generic.Dictionary<string, System.Net.IPEndPoint>
            {
                { "ipv4", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port) },
            },
        }.ToSettings();

    // A cheap deterministic pattern, so a read at any offset can be verified without keeping the
    // 5 GB around in memory to compare against.
    private static byte ExpectedByteAt(long offset) => (byte)(offset % 251);

    private static void WriteDeterministicFile(string path, long size)
    {
        const int ChunkSize = 1024 * 1024;
        var chunk = new byte[ChunkSize];

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);
        long written = 0;
        while (written < size)
        {
            for (var i = 0; i < ChunkSize; i++)
            {
                chunk[i] = ExpectedByteAt(written + i);
            }

            var toWrite = (int)Math.Min(ChunkSize, size - written);
            fs.Write(chunk, 0, toWrite);
            written += toWrite;
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), token);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // Best effort — a temp directory left behind must never fail the test.
        }
    }
}
