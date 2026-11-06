using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// How full tempdb's data files are, and who fills them. Writes <c>tempdb_used_mb</c> (and its user / internal / version-store parts)
/// on every run, so the newest run is always known. <c>tempdb_capacity_mb</c> is what the files can reach: their current size plus
/// the room they may still grow into, limited by max_size and by free space on the volume. It is left out when that room cannot be
/// known (unlimited growth on a server too old to report volume space), so nothing is judged on a guess.
/// <c>tempdb_session_mb</c> lists the biggest user sessions (net allocation, subject = session id).
/// </summary>
public sealed class TempDbCollector : ICollector
{
    public string Name => "tempdb";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    // size and max_size are in 8 KB pages. max_size: -1 = unlimited, 0 = no growth. Only data files (type 0).
    private const string FilesWithVolume = """
        SELECT mf.size, mf.max_size, mf.growth, vs.volume_mount_point, vs.available_bytes
        FROM tempdb.sys.database_files AS mf
        OUTER APPLY sys.dm_os_volume_stats(2, mf.file_id) AS vs
        WHERE mf.type = 0;
        """;

    private const string FilesWithoutVolume = """
        SELECT size, max_size, growth, CAST(NULL AS nvarchar(512)), CAST(NULL AS bigint)
        FROM tempdb.sys.database_files
        WHERE type = 0;
        """;

    // The DMV reports the database it is run in, hence the three-part name.
    private const string Usage = """
        SELECT SUM(CAST(unallocated_extent_page_count AS bigint)),
               SUM(CAST(user_object_reserved_page_count AS bigint)),
               SUM(CAST(internal_object_reserved_page_count AS bigint)),
               SUM(CAST(version_store_reserved_page_count AS bigint))
        FROM tempdb.sys.dm_db_file_space_usage;
        """;

    private const string Sessions = """
        SELECT TOP (3) u.session_id,
               (CAST(u.user_objects_alloc_page_count AS bigint) - u.user_objects_dealloc_page_count
                + u.internal_objects_alloc_page_count - u.internal_objects_dealloc_page_count) * 8 / 1024 AS mb,
               s.login_name, s.host_name, s.program_name
        FROM sys.dm_db_session_space_usage AS u
        JOIN sys.dm_exec_sessions AS s ON s.session_id = u.session_id
        WHERE s.is_user_process = 1
        ORDER BY mb DESC;
        """;

    /// <summary>One data file of tempdb: current size, largest allowed size (null = unlimited), and the free bytes of its volume if known.</summary>
    public sealed record FileInfo(double SizeMb, double? MaxMb, string? Volume, double? VolumeFreeMb);

    /// <summary>
    /// Size now plus room to grow. A file that cannot grow adds nothing; files on one volume share that volume's free space.
    /// Null when an unlimited file sits on a volume whose free space is unknown.
    /// </summary>
    public static double? CapacityMb(IReadOnlyList<FileInfo> files)
    {
        var total = files.Sum(f => f.SizeMb);
        foreach (var group in files.GroupBy(f => f.Volume ?? ""))
        {
            double? room = 0;
            foreach (var f in group)
            {
                if (f.MaxMb is { } max) room += Math.Max(0, max - f.SizeMb);
                else room = null;
                if (room is null) break;
            }
            var free = group.Min(f => f.VolumeFreeMb);
            if (room is null)
            {
                if (free is null) return null;
                room = free;
            }
            else if (free is { } f2) room = Math.Min(room.Value, f2);
            total += room.Value;
        }
        return total;
    }

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var files = new List<FileInfo>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = server.IsAtLeast(10, 50, 2500) ? FilesWithVolume : FilesWithoutVolume;
            cmd.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var size = Convert.ToDouble(r.GetValue(0)) * 8 / 1024;
                var maxPages = Convert.ToInt64(r.GetValue(1));
                var growth = Convert.ToInt64(r.GetValue(2));
                double? max = growth == 0 || maxPages == 0 ? size : maxPages == -1 ? null : maxPages * 8.0 / 1024;
                files.Add(new FileInfo(size, max, r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4) / 1048576.0));
            }
        }

        var sizeMb = files.Sum(f => f.SizeMb);
        double free = 0, user = 0, internalObjects = 0, version = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Usage;
            cmd.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !r.IsDBNull(0))
            {
                free = Convert.ToDouble(r.GetValue(0)) * 8 / 1024;
                user = Convert.ToDouble(r.GetValue(1)) * 8 / 1024;
                internalObjects = Convert.ToDouble(r.GetValue(2)) * 8 / 1024;
                version = Convert.ToDouble(r.GetValue(3)) * 8 / 1024;
            }
        }

        var samples = new List<Sample>
        {
            new("tempdb_used_mb", null, Math.Max(0, sizeMb - free)),
            new("tempdb_user_mb", null, user),
            new("tempdb_internal_mb", null, internalObjects),
            new("tempdb_version_mb", null, version),
        };
        if (CapacityMb(files) is { } capacity) samples.Add(new Sample("tempdb_capacity_mb", null, capacity));

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Sessions;
            cmd.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var mb = Convert.ToDouble(r.GetValue(1));
                if (mb < 1) continue;
                var who = $"{(r.IsDBNull(2) ? "" : r.GetString(2))} @ {(r.IsDBNull(3) ? "" : r.GetString(3))} ({(r.IsDBNull(4) ? "" : r.GetString(4))})";
                samples.Add(new Sample("tempdb_session_mb", Convert.ToInt32(r.GetValue(0)).ToString(), mb, who));
            }
        }
        return CollectionResult.Ok(samples);
    }
}
