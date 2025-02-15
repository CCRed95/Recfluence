﻿using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using Mutuo.Etl.Blob;
using Serilog;
using SysExtensions.Serialization;
using SysExtensions.Threading;
using YtReader.Store;

namespace YtReader {
  public class YtConvertWatchTimeFiles {
    readonly ISimpleFileStore Store;

    public YtConvertWatchTimeFiles(BlobStores stores) => Store = stores.Store(DataStoreType.Root);

    public async Task Convert(ILogger log) {
      var files = (await Store.List("import/watch_time").SelectManyList()).Where(f => f.Path.ExtensionsString == "csv");
      await files.BlockAction(async f => {
        using var stream = await Store.Load(f.Path);
        using var sr = new StreamReader(stream);
        using var csv = new CsvReader(sr, CultureInfo.InvariantCulture) {
          Configuration = {
            Encoding = Encoding.UTF8,
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = r => log.Warning("Error reading csv data at {RowNumber}: {RowData}", r.Row, r.RawRecord)
          }
        };
        var rows = await csv.GetRecordsAsync<dynamic>().ToListAsync();
        await Store.Save(f.Path.Parent.Add($"{f.Path.NameSansExtension}.json.gz"), await rows.ToJsonlGzStream(), log);
      }, parallel: 4);
    }
  }
}