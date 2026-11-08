using Pilewise.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

public interface ISteamLinuxLookup
{
    bool Enabled { get; set; }

    Task StampAsync(
        IEnumerable<CsvGameExportRow> rows,
        IProgress<StoreFetchProgress>? progress = null,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<int, string>> LookupAsync(
        IEnumerable<int> appIds,
        IProgress<StoreFetchProgress>? progress = null,
        CancellationToken ct = default);
}
