﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Humanizer;
using Humanizer.Bytes;
using Mutuo.Etl.Blob;
using Newtonsoft.Json.Linq;
using Serilog;
using SysExtensions.Serialization;
using SysExtensions.Text;
using SysExtensions.Threading;
using YtReader.Db;
using IndexExpression = System.Linq.Expressions.Expression<System.Func<YtReader.Store.WorkCfg>>;

namespace YtReader.Store {
  class WorkCfg {
    public IndexCol[] Cols { get; set; }
    public string     Sql  { get; set; }
    public ByteSize   Size { get; set; }
  }

  public class YtIndexResults {
    readonly SnowflakeConnectionProvider Sf;
    readonly BlobIndex                   BlobIndex;

    public YtIndexResults(BlobStores stores, SnowflakeConnectionProvider sf) {
      Sf = sf;
      BlobIndex = new(stores.Store(DataStoreType.Results));
    }

    public async Task Run(IReadOnlyCollection<string> include, ILogger log, CancellationToken cancel = default) {
      var toRun = new IndexExpression[] {
          () => TopVideos(20_000),
          () => TopChannelVideos(50),
          () => ChannelStatsByPeriod(),
          () => ChannelStatsById(),
          () => VideoRemoved(),
          () => VideoRemovedCaption(),
          () => NarrativeChannels(),
          () => NarrativeVideos(),
          () => UsRecs(),
          () => UsWatch(),
          () => UsFeed()
        }
        .Select(e => new {Expression = e, Name = ((MethodCallExpression) e.Body).Method.Name.Underscore()})
        .Where(t => include == null || include.Contains(t.Name));

      var (res, indexDuration) = await toRun.BlockFunc(async t => {
        var cfg = t.Expression.Compile().Invoke();
        var work = await IndexWork(log, t.Name, cfg.Cols, cfg.Sql, cfg.Size);
        
        return await BlobIndex.SaveIndexedJsonl(work, log, cancel);
      }, parallel: 4, cancel: cancel).WithDuration();

      log.Information("Completed writing indexes files {Indexes} in {Duration}. Starting commit.",
        res.Select(i => i.IndexFilesPath), indexDuration.HumanizeShort());

      if (cancel.IsCancellationRequested) return;

      await res.BlockAction(r => BlobIndex.CommitIndexJson(r, log), parallel: 10, cancel: cancel);
      log.Information("Committed indexes {Indexes}", res.Select(i => i.IndexPath));
    }

    public static string IndexVersion = "v2";

    WorkCfg Work(IndexCol[] cols, string sql, ByteSize? size = default) =>
      new() {Cols = cols, Sql = sql, Size = size ?? 200.Kilobytes()};

    async Task<BlobIndexWork> IndexWork(ILogger log, string name, IndexCol[] cols, string sql, ByteSize size, Action<JObject> onProcessed = null) {
      using var con = await Sf.Open(log);

      var reader = await con.ExecuteReader(name, sql);
      async IAsyncEnumerable<JObject> GetRows() {
        while (await reader.ReadAsync())
          yield return reader.ToSnowflakeJObject().ToCamelCase();
      }

      var path = StringPath.Relative("index", name, IndexVersion);
      return new(path, cols, GetRows(), size, onProcessed);
    }

    #region Channels & Videos

    static readonly IndexCol[] PeriodCols = new[] {"period"}.Select(c => Col(c, writeDistinct: true)).ToArray();

    static IndexCol Col(string dbName, bool inIndex = true, bool writeDistinct = false) => new IndexCol {
      Name = dbName.ToCamelCase(),
      DbName = dbName,
      InIndex = inIndex,
      WriteDistinct = writeDistinct
    };

    /// <summary>Top videos for all channels for a given time period</summary>
    WorkCfg TopVideos(int topPerPeriod) => Work(PeriodCols, TopVideoResSql(rank: topPerPeriod, PeriodCols));

    /// <summary>Top videos from a channel & time period</summary>
    WorkCfg TopChannelVideos(int topPerChannel) {
      var cols = new[] {Col("channel_id")}.Concat(PeriodCols).ToArray();
      return Work(cols, TopVideoResSql(topPerChannel, cols), 300.Kilobytes());
    }

    string TopVideoResSql(int rank, IndexCol[] cols) {
      var indexColString = cols.DbNames().Join(",");
      return $@"with video_ex as (
  select video_id, video_title, upload_date, views as video_views, duration from video_latest
)
select t.video_id
     , video_title
     , channel_id
     , upload_date
     , timediff(seconds, '0'::time, v.duration) as duration_secs
     , concat(period_type, '|', period_value) period
     , views as period_views
     , video_views
     , watch_hours
     , rank() over (partition by {indexColString} order by period_views desc) rank
from ttube_top_videos t
left join video_ex v on v.video_id = t.video_id
  qualify rank<{rank}
order by {indexColString}, rank";
    }

    /// <summary>Aggregate stats for a channel at a given time period</summary>
    WorkCfg ChannelStatsByPeriod() => Work(PeriodCols, ChannelStatsSql(PeriodCols), 100.Kilobytes());

    static readonly IndexCol[] ByChannelCols = {Col("channel_id")};

    /// <summary>Aggregate stats for a channel given a channel</summary>
    WorkCfg ChannelStatsById() => Work(ByChannelCols, ChannelStatsSql(ByChannelCols), 50.Kilobytes());

    static string ChannelStatsSql(IndexCol[] orderCols) =>
      $@"with by_channel as (
  select t.channel_id
       , concat(t.period_type, '|', t.period_value) period
       , sum(views) views
       , sum(watch_hours) watch_hours
  from ttube_top_videos t
  group by t.channel_id, t.period_type, t.period_value
)
select t.*
  , r.latest_refresh
  , r.videos
from by_channel t
       left join ttube_refresh_stats r on r.channel_id=t.channel_id and concat(r.period_type, '|', r.period_value)=t.period
order by {orderCols.DbNames().Join(",")}";

    WorkCfg VideoRemoved() =>
      Work(
        new[] {Col("last_seen"), Col("error_type", inIndex: false, writeDistinct: true)}, @"
select e.*
     , exists(select c.video_id from caption c where e.video_id=c.video_id) has_captions
from video_error e
where platform = 'YouTube'
order by last_seen", 100.Kilobytes());

    WorkCfg VideoRemovedCaption() => Work(new[] {Col("video_id")}, @"
select e.video_id, c.caption, c.offset_seconds from video_error e
inner join caption c on e.video_id = c.video_id
where platform = 'YouTube'
order by video_id, offset_seconds", 100.Kilobytes());

    #endregion

    #region Narrative

    static readonly IndexCol[] NarrativeChannelsCols = {Col("narrative", writeDistinct: true)};

    WorkCfg NarrativeChannels() =>
      Work(NarrativeChannelsCols, $@"
with by_channel as (
  select n.channel_id, n.narrative, sum(v.views) views
  from video_narrative n
         left join video_latest v on v.video_id=n.video_id
  group by n.narrative, n.channel_id
),
s as (
  select n.*
          , cl.channel_title
         , arrayExclude(cl.tags, array_construct('MissingLinkMedia', 'OrganizedReligion', 'Educational')) tags
         , cl.lr
         , logo_url
         , subs
         , substr(cl.description, 0, 301) description
  from by_channel n
           left join channel_latest cl on n.channel_id=cl.channel_id
)
select * from s order by {NarrativeChannelsCols.DbNames().Join(",")}");

    static readonly IndexCol[] NarrativeVideoCols = {Col("narrative", writeDistinct: true), Col("upload_date")};

    WorkCfg NarrativeVideos() => Work(
      NarrativeVideoCols, $@"
with s as (
  select n.narrative
       , n.video_id
       , n.video_title
       , n.channel_id
       , n.support
       , n.supplement
       , v.views::int video_views
       , case
           when n.supplement='manual' then 1
           when n.support='support' then iff(v.upload_date<'2020-12-09 ',0.84/0.96,0.68/0.97)
           when n.support='dispute' then iff(v.upload_date<'2020-12-09 ',0.84/0.94,0.80/0.97)
           else 1
         end * v.views::int as video_views_adjusted
       , v.upload_date::date upload_date
       , ve.error_type
       , timediff(seconds,'0'::time,v.duration) duration_secs
       , n.captions
       , ve.last_seen
  from video_narrative n
         left join video_latest v on n.video_id=v.video_id
         left join video_extra e on e.video_id=v.video_id
         left join video_error ve on ve.video_id=n.video_id
)
select *
from s
order by {NarrativeVideoCols.DbNames().Join(",")}, video_views desc");

    #endregion

    #region Recs

    static readonly IndexCol[] UsRecCols = {
      Col("label", writeDistinct: true),
      Col("from_channel_id", writeDistinct: true)
    };

    WorkCfg UsRecs() => Work(UsRecCols, @$"
with video_date_accounts as (
  select from_video_id, day, count(distinct account) accounts_total
  from (
         select from_video_id, updated::date day, account
         from us_rec
         group by 1, 2, 3
         having max(rank)>5 -- at least x videos per account
       )
  group by 1, 2
  having accounts_total>=12 -- at least x accounts watched the same vid
)
   , full_account_recs as (
  select r.account
       , r.updated::date day
       , m.label
       , r.from_video_id
       , r.to_video_id
       , r.from_channel_id
       , r.from_channel_title
       , r.from_video_title
       , r.to_video_title
       , r.to_channel_id
       , r.to_channel_title
       , d.accounts_total
  from us_rec r
         left join us_test_manual m on m.video_id=r.from_video_id
         inner join video_date_accounts d on d.from_video_id=r.from_video_id and d.day=r.updated::date
  where account<>'Black'
)
   , sets as (
  select from_video_id
       , to_video_id
       , day
       , label
       , array_agg(distinct account) accounts
       , any_value(from_channel_id) from_channel_id
       , any_value(from_video_title) from_video_title
       , any_value(to_video_title) to_video_title
       , any_value(to_channel_id) to_channel_id
       , any_value(to_channel_title) to_channel_title
  from full_account_recs r
  group by 1, 2, 3, 4
)
select *
from sets
order by {UsRecCols.DbNames().Join(",")}", 200.Kilobytes());

    static readonly IndexCol[] VideoSeenCols = {Col("part"), Col("account", writeDistinct: true)};

    static string GetVideoSeen(string table, bool titleInSeen = false) =>
      $@"
with s1 as (
  select w.account
       , w.video_id
       --, any_value(w.video_title) as video_title
       , any_value({(titleInSeen ? "w" : "vl")}.video_title) as video_title
       , any_value(vl.channel_id) as channel_id
       , any_value(vl.channel_title) as channel_title
       , min(w.updated) first_seen
       , max(w.updated) last_seen
       , count(*) as seen_total
  from {table} w
         left join video_latest vl on w.video_id=vl.video_id
  where account<>'Black'
  group by 1, 2
)
select *
     , iff(row_number() over (partition by account order by seen_total desc)<100, 'featured', null) part
      , percent_rank() over (partition by account order by seen_total) percentile
from s1
order by {VideoSeenCols.DbNames().Join(",")}, percentile desc";

    WorkCfg UsWatch() => Work(VideoSeenCols, GetVideoSeen("us_watch"), 100.Kilobytes());
    WorkCfg UsFeed() => Work(VideoSeenCols, GetVideoSeen("us_feed", titleInSeen: true), 100.Kilobytes());

    #endregion
  }
}