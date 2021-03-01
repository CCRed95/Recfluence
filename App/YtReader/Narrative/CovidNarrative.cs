﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AirtableApiClient;
using Newtonsoft.Json.Linq;
using Serilog;
using SysExtensions.Collections;
using SysExtensions.Serialization;
using SysExtensions.Threading;
using YtReader.Db;
using YtReader.Store;

// ReSharper disable InconsistentNaming

namespace YtReader.Narrative {
  public record AirtableCfg(string ApiKey = null, string BaseId = "appwfe3XfYqxn7v7I");
  public record NarrativesCfg(string CovidAirtable = "Covid");
  public record VideoIdRow(string videoId);

  public record CovidNarrative(NarrativesCfg Cfg, AirtableCfg AirCfg, SnowflakeConnectionProvider Sf) {
    public async Task MargeIntoAirtable(ILogger log) {
      using var airTable = new AirtableBase(AirCfg.ApiKey, AirCfg.BaseId);
      var airRows = await airTable.Rows<VideoIdRow>(Cfg.CovidAirtable, new[] {"videoId"}).ToListAsync()
        .Then(rows => rows.ToKeyedCollection(r => r.Fields.videoId));
      using var db = await Sf.Open(log);
      var batchSize = 10;
      await db.ReadAsJson("covid narrative", @"
select video_id
     , video_title
     , channel_id
     , channel_title
     , views
     , description
     , subreddit
     , arrayjoin(captions,object_construct('video_id',video_id)
  ,'\n','[{offset}](https://youtube.com/watch?v={video_id}&t={offset}) {caption}') captions
from covid_narrative_review
order by views desc
limit 1000")
        .Select(v => v.ToCamelCase())
        .Batch(batchSize).BlockAction(async (rows, i) => {
          var forCreate = rows.Where(r => !airRows.ContainsKey(r.Value<string>("videoId"))).Select(r => r.ToAirFields()).ToArray();
          var res = await airTable.CreateMultipleRecords(Cfg.CovidAirtable, forCreate);
          log.Information("CovidNarrative - created airtable records {Rows}", (i + 1) * batchSize);
          res.EnsureSuccess();
        });
    }
  }

  public static class AirtableExtensions {
    public static async IAsyncEnumerable<AirtableRecord<T>> Rows<T>(this AirtableBase at, string table, string[] fields = null) {
      string offset = null;
      while (true) {
        var res = await at.ListRecords<T>(table, offset, fields);
        res.EnsureSuccess();
        foreach (var r in res.Records)
          yield return r;
        offset = res.Offset;
        if (offset == null) break;
      }
    }

    public static async IAsyncEnumerable<AirtableRecord> Rows(this AirtableBase at, string table, string[] fields = null) {
      string offset = null;
      while (true) {
        var res = await at.ListRecords(table, offset, fields);
        res.EnsureSuccess();
        foreach (var r in res.Records)
          yield return r;
        offset = res.Offset;
        if (offset == null) break;
      }
    }

    public static void EnsureSuccess(this AirtableApiResponse res) {
      if (!res.Success) throw res.AirtableApiError as Exception ?? new InvalidOperationException("Airtable unknown error");
    }

    public static Fields ToAirFields(this JObject j) {
      var dic = j.ToObject<Dictionary<string, object>>();
      var fields = new Fields {FieldsCollection = dic};
      return fields;
    }

    public static JObject RecordJObject(this AirtableRecord record) {
      var j = new JObject(new JProperty("id", record.Id), new JProperty("createdTime", record.CreatedTime));
      foreach (var field in record.Fields)
        j.Add(field.Key, JToken.FromObject(field.Value));
      return j;
    }
  }
}