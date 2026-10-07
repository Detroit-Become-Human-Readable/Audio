using System.IO.Compression;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DetroitAudio.Core;
using Microsoft.Data.Sqlite;

namespace DetroitAudio.Indexing;

public sealed record CatalogScope
{
    public ImmutableArray<string> BankKeys { get; }
    public ImmutableArray<string> EventKeys { get; }
    public CatalogScope(IEnumerable<string>? BankKeys = null, IEnumerable<string>? EventKeys = null)
    {
        this.BankKeys = (BankKeys ?? []).Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.Ordinal).ToImmutableArray();
        this.EventKeys = (EventKeys ?? []).Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.Ordinal).ToImmutableArray();
    }
}

public sealed record CatalogQuery(string Search = "", string? BankKey = null, string? EventKey = null, string? Language = null,
    string? Category = null, string? Codec = null, MediaState? State = null, NameKind? NameKind = null, bool Dialogue = false, int Offset = 0, int Limit = 250, string Sort = "Name", bool Descending = false, string? Archive = null, CatalogScope? Scope = null);
public sealed record CatalogPage(IReadOnlyList<MediaEntry> Entries, int Total);

public sealed class CatalogStore : ICatalogSession
{
    public const int SchemaVersion = 10;
    private readonly string databasePath, snapshotPath;
    private readonly object cacheGate = new();
    private readonly Dictionary<string, (BankInfo Bank, long Access)> graphCache = [];
    private readonly Dictionary<string, (BrowserLayout Layout, long Access)> browserLayoutCache = new(StringComparer.Ordinal);
    private long access;
    private long browserCacheGeneration;
    private CatalogHeader? header;
    public CatalogHeader Header => header ??= ReadHeader() ?? throw new InvalidDataException("The catalog header is unavailable.");
    public int CachedGraphCount { get { lock (cacheGate) return graphCache.Count; } }
    public int CachedBrowserLayoutCount { get { lock (cacheGate) return browserLayoutCache.Count; } }
    public long CachedBrowserLayoutBytes { get { lock (cacheGate) return browserLayoutCache.Values.Sum(entry=>entry.Layout.EstimatedBytes); } }
    public CatalogStore(string directory)
    {
        Directory.CreateDirectory(directory); databasePath = Path.Combine(directory, "catalog.db"); snapshotPath = Path.Combine(directory, "catalog.json.br");
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA busy_timeout=5000;"; command.ExecuteNonQuery(); return connection;
    }
    public CatalogHeader? ReadHeader(string? fingerprint = null, CancellationToken token = default)
    {
        if (!File.Exists(databasePath)) return null;
        try
        {
            return Execute(token, command =>
            {
                command.CommandText = "SELECT value FROM metadata WHERE name='header' AND (SELECT value FROM metadata WHERE name='schema')=$schema";
                command.Parameters.AddWithValue("$schema", SchemaVersion.ToString());
                var value = command.ExecuteScalar() as string;
                var result = value is null ? null : JsonSerializer.Deserialize<CatalogHeader>(value);
                return result is not null && (fingerprint is null || result.Fingerprint == fingerprint) ? result : null;
            });
        }
        catch (Exception error) when (error is SqliteException or JsonException or InvalidDataException or IOException) { return null; }
    }
    // Full snapshots remain available for command-line audits, not the desktop browsing path.
    public AudioCatalog? Load(string fingerprint, CancellationToken token = default)
    {
        if (ReadHeader(fingerprint, token) is null || !File.Exists(snapshotPath)) return null;
        try { using var file = File.OpenRead(snapshotPath); using var compressed = new BrotliStream(file, CompressionMode.Decompress); var result = JsonSerializer.DeserializeAsync<AudioCatalog>(compressed, cancellationToken: token).AsTask().GetAwaiter().GetResult(); return result?.Fingerprint == fingerprint ? result : null; }
        catch (Exception error) when (error is JsonException or IOException) { return null; }
    }
    public void Save(AudioCatalog catalog, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var temporary = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary)) using (var compressed = new BrotliStream(file, CompressionLevel.Fastest)) JsonSerializer.SerializeAsync(compressed, catalog, cancellationToken: token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            using var connection = Open(); using var interruption = token.Register(() => { if (connection.Handle is { } handle) SQLitePCL.raw.sqlite3_interrupt(handle); });
            using var transaction = connection.BeginTransaction(); using var schema = connection.CreateCommand(); schema.Transaction = transaction;
            schema.CommandText = """
                DROP TABLE IF EXISTS media_search; DROP TABLE IF EXISTS browser_group_members; DROP TABLE IF EXISTS browser_blocks; DROP TABLE IF EXISTS media_groups; DROP TABLE IF EXISTS media; DROP TABLE IF EXISTS event_media; DROP TABLE IF EXISTS metadata; DROP TABLE IF EXISTS banks; DROP TABLE IF EXISTS events; DROP TABLE IF EXISTS object_owners;
                CREATE TABLE metadata(name TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE media(rid INTEGER PRIMARY KEY,key TEXT UNIQUE NOT NULL,name TEXT NOT NULL,search TEXT NOT NULL,bank TEXT NOT NULL,bank_name TEXT NOT NULL,language TEXT NOT NULL,category TEXT NOT NULL,codec TEXT NOT NULL,state INTEGER NOT NULL,name_kind INTEGER NOT NULL,bytes INTEGER NOT NULL,duration REAL,id INTEGER NOT NULL,json TEXT NOT NULL,source TEXT NOT NULL,file TEXT NOT NULL,offset INTEGER NOT NULL,complete INTEGER NOT NULL,container INTEGER NOT NULL,availability INTEGER NOT NULL,riff INTEGER NOT NULL,channels INTEGER NOT NULL,rate INTEGER NOT NULL);
                CREATE TABLE event_media(event TEXT NOT NULL,media TEXT NOT NULL,PRIMARY KEY(event,media)); CREATE INDEX event_media_media ON event_media(media);
                CREATE TABLE media_groups(media TEXT PRIMARY KEY,group_key TEXT NOT NULL,label TEXT NOT NULL); CREATE INDEX media_groups_key ON media_groups(group_key,media);
                CREATE TABLE browser_blocks(sort TEXT NOT NULL,descending INTEGER NOT NULL,block_order INTEGER NOT NULL,media TEXT,group_key TEXT,label TEXT NOT NULL,members INTEGER NOT NULL,PRIMARY KEY(sort,descending,block_order));
                CREATE TABLE browser_group_members(sort TEXT NOT NULL,descending INTEGER NOT NULL,group_key TEXT NOT NULL,member_order INTEGER NOT NULL,media TEXT NOT NULL,PRIMARY KEY(sort,descending,group_key,member_order));
                CREATE TABLE banks(key TEXT PRIMARY KEY,id INTEGER NOT NULL,summary TEXT NOT NULL,graph BLOB NOT NULL); CREATE INDEX banks_id ON banks(id);
                CREATE TABLE events(key TEXT PRIMARY KEY,bank TEXT NOT NULL,id INTEGER NOT NULL,name TEXT NOT NULL,dialogue INTEGER NOT NULL,behavior INTEGER NOT NULL,link INTEGER NOT NULL,json TEXT NOT NULL); CREATE INDEX events_bank ON events(bank,name,key);
                CREATE TABLE object_owners(id INTEGER NOT NULL,bank TEXT NOT NULL,type INTEGER NOT NULL,PRIMARY KEY(bank,id)); CREATE INDEX object_owners_id ON object_owners(id,bank);
                CREATE VIRTUAL TABLE media_search USING fts5(search, content='media', content_rowid='rid', tokenize='trigram');
                """;
            schema.ExecuteNonQuery();
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            var columns = new[] { "key","name","search","bank","bank_name","language","category","codec","state","name_kind","bytes","duration","id","json","source","file","offset","complete","container","availability","riff","channels","rate" };
            insert.CommandText = "INSERT INTO media(" + string.Join(',', columns) + ") VALUES(" + string.Join(',', columns.Select(column => "$" + column)) + ")";
            foreach (var column in columns) insert.Parameters.Add(new SqliteParameter("$" + column, null)); insert.Prepare();
            using var link = connection.CreateCommand(); link.Transaction = transaction; link.CommandText = "INSERT OR IGNORE INTO event_media VALUES($event,$media)"; link.Parameters.Add(new("$event", null)); link.Parameters.Add(new("$media", null)); link.Prepare();
            var completed = 0;
            foreach (var media in catalog.Media)
            {
                token.ThrowIfCancellationRequested();
                var values = new object?[] { media.Key,media.DisplayName,string.Join(' ',new[]{media.DisplayName,media.Name,media.Id.ToString(),media.Id.ToString("X8"),media.BankName,media.Subtitle}.Concat(media.Names.Select(name=>name.Text)).Concat(media.Aliases)).ToLowerInvariant(),media.BankKey,media.BankName,media.Language,media.Category,media.Codec,(int)media.State,(int)media.NameKind,media.Slice.Length,media.Duration,(long)media.Id,JsonSerializer.Serialize(media),Path.GetFileName(media.Slice.FilePath),media.Slice.FilePath,media.Slice.Offset,(int)media.Completeness,(int)media.ContainerValidity,(int)media.Availability,media.IsRiff?1:0,media.Channels,media.SampleRate };
                for (var i=0;i<columns.Length;i++) insert.Parameters[i].Value=values[i] ?? DBNull.Value; insert.ExecuteNonQuery();
                foreach (var key in media.EventKeys) { link.Parameters[0].Value=key; link.Parameters[1].Value=media.Key; link.ExecuteNonQuery(); }
                if (++completed % 500 == 0) progress?.Report(new("Catalog",completed,catalog.Media.Count));
            }
            foreach (var entry in catalog.Events)
            {
                foreach (var (eventKey,mediaKey) in entry.MediaKeys.Select(key=>(entry.Key,key))
                    .Concat(entry.MediaLinks.SelectMany(item=>item.PlaybackEventKey is {Length:>0} playbackKey?new[]{(entry.Key,item.MediaKey),(playbackKey,item.MediaKey)}:new[]{(entry.Key,item.MediaKey)}))
                    .Distinct())
                { token.ThrowIfCancellationRequested(); link.Parameters[0].Value=eventKey;link.Parameters[1].Value=mediaKey;link.ExecuteNonQuery(); }
            }
            using var bankInsert = connection.CreateCommand(); bankInsert.Transaction=transaction;bankInsert.CommandText="INSERT INTO banks VALUES($key,$id,$summary,$graph)";foreach(var key in new[]{"key","id","summary","graph"})bankInsert.Parameters.Add(new("$"+key,null));bankInsert.Prepare();
            using var objectInsert=connection.CreateCommand();objectInsert.Transaction=transaction;objectInsert.CommandText="INSERT INTO object_owners VALUES($id,$bank,$type)";foreach(var key in new[]{"id","bank","type"})objectInsert.Parameters.Add(new("$"+key,null));objectInsert.Prepare();
            foreach(var bank in catalog.Banks)
            {
                token.ThrowIfCancellationRequested();var summary=new BankInfo{Key=bank.Key,Id=bank.Id,Name=bank.Name,Version=bank.Version,HasFeedback=bank.HasFeedback,LanguageId=bank.LanguageId,Slice=bank.Slice,Chunks=bank.Chunks,Names=bank.Names,Diagnostics=bank.Diagnostics,IndexedObjectCount=bank.Objects.Count};
                using var buffer=new MemoryStream();using(var compressor=new BrotliStream(buffer,CompressionLevel.Fastest,true))JsonSerializer.Serialize(compressor,bank);
                bankInsert.Parameters[0].Value=bank.Key;bankInsert.Parameters[1].Value=(long)bank.Id;bankInsert.Parameters[2].Value=JsonSerializer.Serialize(summary);bankInsert.Parameters[3].Value=buffer.ToArray();bankInsert.ExecuteNonQuery();
                foreach(var obj in bank.Objects.Values){objectInsert.Parameters[0].Value=(long)obj.Id;objectInsert.Parameters[1].Value=bank.Key;objectInsert.Parameters[2].Value=(int)obj.Type;objectInsert.ExecuteNonQuery();}
            }
            using var eventInsert=connection.CreateCommand();eventInsert.Transaction=transaction;eventInsert.CommandText="INSERT INTO events VALUES($key,$bank,$id,$name,$dialogue,$behavior,$link,$json)";foreach(var key in new[]{"key","bank","id","name","dialogue","behavior","link","json"})eventInsert.Parameters.Add(new("$"+key,null));eventInsert.Prepare();
            foreach(var entry in catalog.Events){token.ThrowIfCancellationRequested();var values=new object[]{entry.Key,entry.BankKey,(long)entry.Id,entry.DisplayName,entry.IsDialogue?1:0,(int)entry.Behavior,(int)entry.LinkStatus,JsonSerializer.Serialize(entry)};for(var i=0;i<values.Length;i++)eventInsert.Parameters[i].Value=values[i];eventInsert.ExecuteNonQuery();}
            using(var groupInsert=connection.CreateCommand())
            {
                groupInsert.Transaction=transaction;groupInsert.CommandText="INSERT INTO media_groups VALUES($media,$group,$label)";
                groupInsert.Parameters.Add(new("$media",null));groupInsert.Parameters.Add(new("$group",null));groupInsert.Parameters.Add(new("$label",null));groupInsert.Prepare();
                foreach(var assignment in CatalogGrouping.BuildAssignments(catalog,token))
                {
                    token.ThrowIfCancellationRequested();groupInsert.Parameters[0].Value=assignment.MediaKey;groupInsert.Parameters[1].Value=assignment.GroupKey;groupInsert.Parameters[2].Value=assignment.Label;groupInsert.ExecuteNonQuery();
                }
            }
            using var index=connection.CreateCommand();index.Transaction=transaction;
            foreach(var column in new[]{"name","id","bank_name","language","duration","bytes"})foreach(var direction in new[]{"ASC","DESC"}){token.ThrowIfCancellationRequested();var collation=column is "name" or "bank_name" or "language"?" COLLATE NOCASE":"";index.CommandText=$"CREATE INDEX media_sort_{column}_{direction} ON media({column}{collation} {direction},key ASC)";index.ExecuteNonQuery();}
            foreach(var column in new[]{"bank","language","category","codec","source"}){index.CommandText=$"CREATE INDEX media_filter_{column} ON media({column})";index.ExecuteNonQuery();}
            BuildBrowserLayouts(connection,transaction,token);
            progress?.Report(new("Search index",0,1));index.CommandText="INSERT INTO media_search(media_search) VALUES('rebuild')";index.ExecuteNonQuery();
            var next=new CatalogHeader(catalog.SourcePath,catalog.Fingerprint,catalog.Resources.Count,catalog.Banks.Count,catalog.Media.Count,catalog.Events.Count,catalog.Diagnostics);
            using var metadata=connection.CreateCommand();metadata.Transaction=transaction;metadata.CommandText="INSERT INTO metadata VALUES('schema',$schema),('fingerprint',$fingerprint),('header',$header)";metadata.Parameters.AddWithValue("$schema",SchemaVersion.ToString());metadata.Parameters.AddWithValue("$fingerprint",catalog.Fingerprint);metadata.Parameters.AddWithValue("$header",JsonSerializer.Serialize(next));metadata.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            var backup = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".previous";
            if (File.Exists(snapshotPath)) File.Move(snapshotPath, backup);
            try
            {
                File.Move(temporary, snapshotPath);
                transaction.Commit();
            }
            catch
            {
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
                if (File.Exists(backup)) File.Move(backup, snapshotPath);
                throw;
            }
            header=next;lock(cacheGate){graphCache.Clear();browserLayoutCache.Clear();browserCacheGeneration++;}
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private const string RowColumns="key,id,name,bank,bank_name,language,category,codec,duration,file,offset,bytes,state,name_kind,complete,container,availability,riff,channels,rate";
    public int Count(CatalogQuery query,CancellationToken token=default)=>Execute(token,command=>{command.CommandText="SELECT COUNT(*) FROM media"+BuildWhere(command,query,token);return Convert.ToInt32(command.ExecuteScalar());});
    private static string Order(CatalogQuery query)
    {
        var column=query.Sort switch{"ID"=>"id","Bank"=>"bank_name","Language"=>"language","Duration"=>"duration","Size"=>"bytes",_=>"name"};var collation=column is "name" or "bank_name" or "language"?" COLLATE NOCASE":"";
        return column+collation+(query.Descending?" DESC":" ASC")+",key ASC";
    }
    private static string BrowserOrder(CatalogQuery query)
    {
        var column=BrowserColumn(query.Sort);
        var collation=column is "name" or "bank_name" or "language"?" COLLATE NOCASE":"";
        return "m."+column+collation+(query.Descending?" DESC":" ASC")+",m.key ASC";
    }
    private static string BrowserColumn(string? sort)=>sort switch{"ID"=>"id","Bank"=>"bank_name","Language"=>"language","Duration"=>"duration","Size"=>"bytes",_=>"name"};
    private static string BrowserSortName(string? sort)=>sort switch{"ID"=>"ID","Bank"=>"Bank","Language"=>"Language","Duration"=>"Duration","Size"=>"Size",_=>"Name"};
    private static bool CanUseUnfilteredBrowserLayout(CatalogQuery query)=>string.IsNullOrWhiteSpace(query.Search)&&query.BankKey is null&&query.EventKey is null&&query.Language is null&&query.Category is null&&query.Codec is null&&query.State is null&&query.NameKind is null&&!query.Dialogue&&query.Archive is null&&query.Scope is null;
    private static void BuildBrowserLayouts(SqliteConnection connection,SqliteTransaction transaction,CancellationToken token)
    {
        using var read=connection.CreateCommand();read.Transaction=transaction;
        using var blockInsert=connection.CreateCommand();blockInsert.Transaction=transaction;blockInsert.CommandText="INSERT INTO browser_blocks VALUES($sort,$descending,$order,$media,$group,$label,$members)";
        foreach(var key in new[]{"$sort","$descending","$order","$media","$group","$label","$members"})blockInsert.Parameters.Add(new(key,null));blockInsert.Prepare();
        using var memberInsert=connection.CreateCommand();memberInsert.Transaction=transaction;memberInsert.CommandText="INSERT INTO browser_group_members VALUES($sort,$descending,$group,$member_order,$media)";
        foreach(var key in new[]{"$sort","$descending","$group","$member_order","$media"})memberInsert.Parameters.Add(new(key,null));memberInsert.Prepare();
        foreach(var sort in new[]{"Name","ID","Bank","Language","Duration","Size"})
        foreach(var descending in new[]{false,true})
        {
            token.ThrowIfCancellationRequested();var column=BrowserColumn(sort);var collation=column is "name" or "bank_name" or "language"?" COLLATE NOCASE":"";
            read.CommandText=$"SELECT m.key,g.group_key,g.label FROM media m LEFT JOIN media_groups g ON g.media=m.key ORDER BY m.{column}{collation}{(descending?" DESC":" ASC")},m.key ASC";
            var ordered=new List<(string Key,string? GroupKey,string Label)>();var membersByGroup=new Dictionary<string,List<string>>(StringComparer.Ordinal);var labels=new Dictionary<string,string>(StringComparer.Ordinal);
            using(var reader=read.ExecuteReader())while(reader.Read())
            {
                token.ThrowIfCancellationRequested();var key=reader.GetString(0);var group=reader.IsDBNull(1)?null:reader.GetString(1);var label=reader.IsDBNull(2)?"":reader.GetString(2);ordered.Add((key,group,label));
                if(group is not null){if(!membersByGroup.TryGetValue(group,out var members))membersByGroup[group]=members=[];members.Add(key);labels.TryAdd(group,label);}
            }
            var emitted=new HashSet<string>(StringComparer.Ordinal);var blockOrder=0;
            foreach(var row in ordered)
            {
                token.ThrowIfCancellationRequested();
                if(row.GroupKey is { } group&&membersByGroup[group].Count>=2)
                {
                    if(!emitted.Add(group))continue;
                    var members=membersByGroup[group];blockInsert.Parameters[0].Value=sort;blockInsert.Parameters[1].Value=descending?1:0;blockInsert.Parameters[2].Value=blockOrder++;blockInsert.Parameters[3].Value=DBNull.Value;blockInsert.Parameters[4].Value=group;blockInsert.Parameters[5].Value=labels[group];blockInsert.Parameters[6].Value=members.Count;blockInsert.ExecuteNonQuery();
                    for(var memberOrder=0;memberOrder<members.Count;memberOrder++){if((memberOrder&511)==0)token.ThrowIfCancellationRequested();memberInsert.Parameters[0].Value=sort;memberInsert.Parameters[1].Value=descending?1:0;memberInsert.Parameters[2].Value=group;memberInsert.Parameters[3].Value=memberOrder;memberInsert.Parameters[4].Value=members[memberOrder];memberInsert.ExecuteNonQuery();}
                }
                else
                {
                    blockInsert.Parameters[0].Value=sort;blockInsert.Parameters[1].Value=descending?1:0;blockInsert.Parameters[2].Value=blockOrder++;blockInsert.Parameters[3].Value=row.Key;blockInsert.Parameters[4].Value=DBNull.Value;blockInsert.Parameters[5].Value="";blockInsert.Parameters[6].Value=1;blockInsert.ExecuteNonQuery();
                }
            }
        }
    }
    private static MediaRow ReadMediaRow(SqliteDataReader reader,int start)=>new(reader.GetString(start),(uint)reader.GetInt64(start+1),reader.GetString(start+2),reader.GetString(start+3),reader.GetString(start+4),reader.GetString(start+5),reader.GetString(start+6),reader.GetString(start+7),reader.IsDBNull(start+8)?null:reader.GetDouble(start+8),new(reader.GetString(start+9),reader.GetInt64(start+10),reader.GetInt64(start+11)),(MediaState)reader.GetInt32(start+12),(NameKind)reader.GetInt32(start+13),(MediaCompleteness)reader.GetInt32(start+14),(ContainerValidity)reader.GetInt32(start+15),(SourceAvailability)reader.GetInt32(start+16),reader.GetInt32(start+17)!=0,reader.GetInt32(start+18),reader.GetInt32(start+19));
    private static void PrepareExpanded(SqliteCommand command,IReadOnlySet<string>? groupKeys,CancellationToken token)
    {
        using(var create=command.Connection!.CreateCommand()){create.CommandText="CREATE TEMP TABLE catalog_expanded_groups(key TEXT PRIMARY KEY)";create.ExecuteNonQuery();}
        if(groupKeys is null||groupKeys.Count==0)return;
        FillTempKeys(command.Connection!,"catalog_expanded_groups",groupKeys,token);
    }
    private static string PageSql(SqliteCommand command,CatalogQuery query,string columns,CancellationToken token=default)
    {
        var where=BuildWhere(command,query,token);command.Parameters.AddWithValue("$limit",Math.Clamp(query.Limit,1,1000));command.Parameters.AddWithValue("$offset",Math.Max(0,query.Offset));
        // Sorting never carries the evidence JSON into SQLite's temporary sort records.
        return "SELECT "+columns+" FROM media WHERE rid IN(SELECT rid FROM media"+where+" ORDER BY "+Order(query)+" LIMIT $limit OFFSET $offset) ORDER BY "+Order(query);
    }
    public IReadOnlyList<MediaRow> ReadRows(CatalogQuery query,CancellationToken token=default)=>Execute(token,command=>
    {
        command.CommandText=PageSql(command,query,RowColumns,token);using var reader=command.ExecuteReader();var rows=new List<MediaRow>();while(reader.Read()){token.ThrowIfCancellationRequested();rows.Add(new(reader.GetString(0),(uint)reader.GetInt64(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.IsDBNull(8)?null:reader.GetDouble(8),new(reader.GetString(9),reader.GetInt64(10),reader.GetInt64(11)),(MediaState)reader.GetInt32(12),(NameKind)reader.GetInt32(13),(MediaCompleteness)reader.GetInt32(14),(ContainerValidity)reader.GetInt32(15),(SourceAvailability)reader.GetInt32(16),reader.GetInt32(17)!=0,reader.GetInt32(18),reader.GetInt32(19)));}return (IReadOnlyList<MediaRow>)rows;
    });
    public int CountBrowserRows(CatalogQuery query,bool groupSimilar,IReadOnlySet<string>? expandedGroupKeys=null,CancellationToken token=default)
    {
        if(!groupSimilar)return Count(query,token);
        var layout=GetBrowserLayout(query,token);var count=layout.Blocks.Length;
        if(expandedGroupKeys is not null)foreach(var key in expandedGroupKeys){token.ThrowIfCancellationRequested();if(layout.Groups.TryGetValue(key,out var group))count+=group.MemberCount;}
        return count;
    }
    public IReadOnlyList<BrowserRow> ReadBrowserRows(CatalogQuery query,bool groupSimilar,IReadOnlySet<string>? expandedGroupKeys,int offset,int limit,CancellationToken token=default)
    {
        if(!groupSimilar)return ReadRows(query with{Offset=offset,Limit=limit},token).Select(BrowserRow.ForMedia).ToArray();
        var layout=GetBrowserLayout(query,token);var start=Math.Max(0,offset);var end=start+Math.Clamp(limit,1,1000);var descriptors=new List<BrowserDescriptor>(end-start);var rowPosition=0;
        foreach(var block in layout.Blocks)
        {
            token.ThrowIfCancellationRequested();var expanded=block.GroupKey is not null&&expandedGroupKeys?.Contains(block.GroupKey)==true;var blockRows=block.GroupKey is null?1:expanded?block.MemberCount+1:1;
            if(block.GroupKey is null)
            {
                if(rowPosition>=start&&rowPosition<end)descriptors.Add(new(BrowserRowKind.Media,block.MediaKey!,null,"",0));
            }
            else
            {
                if(rowPosition>=start&&rowPosition<end)descriptors.Add(new(BrowserRowKind.GroupHeader,"group:"+block.GroupKey,block.GroupKey,block.Label,block.MemberCount));
                if(expanded)
                    for(var index=0;index<block.MemberCount;index++)
                    {
                        var memberPosition=rowPosition+1+index;if(memberPosition>=start&&memberPosition<end)descriptors.Add(new(BrowserRowKind.Media,layout.MemberKeys[block.MemberStart+index],block.GroupKey,"",0));
                    }
            }
            rowPosition+=blockRows;if(rowPosition>=end)break;
        }
        var mediaRows=ReadRowsByKeys(descriptors.Where(item=>item.Kind==BrowserRowKind.Media).Select(item=>item.Key).ToArray(),token).ToDictionary(row=>row.Key,StringComparer.Ordinal);
        return descriptors.Select(item=>item.Kind==BrowserRowKind.GroupHeader?BrowserRow.ForGroup(item.GroupKey!,item.Label,item.Count):new(BrowserRowKind.Media,item.Key,item.GroupKey,Media:mediaRows[item.Key])).ToArray();
    }

    private IReadOnlyList<MediaRow> ReadRowsByKeys(IReadOnlyList<string> keys,CancellationToken token)
    {
        return Execute(token,command=>
        {
            if(keys.Count==0)return (IReadOnlyList<MediaRow>)Array.Empty<MediaRow>();
            var parameters=keys.Select((_,index)=>"$key"+index).ToArray();command.CommandText="SELECT "+RowColumns+" FROM media WHERE key IN("+string.Join(',',parameters)+")";
            for(var index=0;index<keys.Count;index++){token.ThrowIfCancellationRequested();command.Parameters.AddWithValue(parameters[index],keys[index]);}
            using var reader=command.ExecuteReader();var rows=new List<MediaRow>(keys.Count);while(reader.Read()){token.ThrowIfCancellationRequested();rows.Add(ReadMediaRow(reader,0));}return (IReadOnlyList<MediaRow>)rows;
        });
    }
    private BrowserLayout GetBrowserLayout(CatalogQuery query,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var cacheKey=BrowserLayoutKey(query);long generation;
        lock(cacheGate)
        {
            if(browserLayoutCache.TryGetValue(cacheKey,out var cached)){browserLayoutCache[cacheKey]=(cached.Layout,++access);return cached.Layout;}
            generation=browserCacheGeneration;
        }
        var built=CanUseUnfilteredBrowserLayout(query)?ReadPersistedBrowserLayout(query,token):BuildFilteredBrowserLayout(query,token);
        token.ThrowIfCancellationRequested();
        lock(cacheGate)
        {
            if(generation!=browserCacheGeneration)return built;
            if(browserLayoutCache.TryGetValue(cacheKey,out var raced)){browserLayoutCache[cacheKey]=(raced.Layout,++access);return raced.Layout;}
            while(browserLayoutCache.Count>=8)browserLayoutCache.Remove(browserLayoutCache.MinBy(pair=>pair.Value.Access).Key);
            browserLayoutCache[cacheKey]=(built,++access);return built;
        }
    }
    private BrowserLayout ReadPersistedBrowserLayout(CatalogQuery query,CancellationToken token)
    {
        var sort=BrowserSortName(query.Sort);var descending=query.Descending?1:0;
        var orderedMembers=Execute(token,command=>
        {
            command.CommandText="SELECT group_key,media FROM browser_group_members WHERE sort=$sort AND descending=$descending ORDER BY group_key,member_order";command.Parameters.AddWithValue("$sort",sort);command.Parameters.AddWithValue("$descending",descending);
            using var reader=command.ExecuteReader();var groups=new Dictionary<string,List<string>>(StringComparer.Ordinal);while(reader.Read()){token.ThrowIfCancellationRequested();var key=reader.GetString(0);if(!groups.TryGetValue(key,out var members))groups[key]=members=[];members.Add(reader.GetString(1));}return groups;
        });
        return Execute(token,command=>
        {
            command.CommandText="SELECT media,group_key,label,members FROM browser_blocks WHERE sort=$sort AND descending=$descending ORDER BY block_order";command.Parameters.AddWithValue("$sort",sort);command.Parameters.AddWithValue("$descending",descending);
            using var reader=command.ExecuteReader();var blocks=new List<BrowserLayoutBlock>();var members=new List<string>();var groups=new Dictionary<string,BrowserLayoutBlock>(StringComparer.Ordinal);
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if(reader.IsDBNull(1)){blocks.Add(new(null,"",reader.GetString(0),0,0));continue;}
                var key=reader.GetString(1);if(!orderedMembers.TryGetValue(key,out var groupMembers))throw new InvalidDataException("The browser group member index is incomplete.");
                var block=new BrowserLayoutBlock(key,reader.GetString(2),null,members.Count,groupMembers.Count);blocks.Add(block);groups.Add(key,block);members.AddRange(groupMembers);
            }
            return CreateBrowserLayout(blocks,members,groups);
        });
    }
    private BrowserLayout BuildFilteredBrowserLayout(CatalogQuery query,CancellationToken token)
    {
        var builders=new List<BrowserLayoutBuilder>();var groupsByKey=new Dictionary<string,BrowserLayoutBuilder>(StringComparer.Ordinal);
        Execute(token,command=>
        {
            var where=BuildWhere(command,query,token);command.CommandText="SELECT m.key,g.group_key,g.label FROM media m LEFT JOIN media_groups g ON g.media=m.key"+where+" ORDER BY "+BrowserOrder(query);
            using var reader=command.ExecuteReader();while(reader.Read())
            {
                token.ThrowIfCancellationRequested();var mediaKey=reader.GetString(0);
                if(reader.IsDBNull(1)){builders.Add(new(null,"",mediaKey));continue;}
                var groupKey=reader.GetString(1);
                if(!groupsByKey.TryGetValue(groupKey,out var builder)){builder=new(groupKey,reader.GetString(2));groupsByKey[groupKey]=builder;builders.Add(builder);}
                builder.Members.Add(mediaKey);
            }
            return 0;
        });
        var blocks=new List<BrowserLayoutBlock>(builders.Count);var members=new List<string>();var groups=new Dictionary<string,BrowserLayoutBlock>(StringComparer.Ordinal);
        foreach(var builder in builders)
        {
            token.ThrowIfCancellationRequested();
            if(builder.GroupKey is not null&&builder.Members.Count>=2)
            {
                var block=new BrowserLayoutBlock(builder.GroupKey,builder.Label,null,members.Count,builder.Members.Count);blocks.Add(block);groups.Add(builder.GroupKey,block);members.AddRange(builder.Members);
            }
            else blocks.Add(new(null,"",builder.Members[0],0,0));
        }
        return CreateBrowserLayout(blocks,members,groups);
    }
    private static BrowserLayout CreateBrowserLayout(List<BrowserLayoutBlock> blocks,List<string> members,Dictionary<string,BrowserLayoutBlock> groups)
    {
        long bytes=blocks.Count*48L+members.Count*IntPtr.Size+groups.Count*48L;
        foreach(var block in blocks)bytes+=BrowserStringBytes(block.GroupKey)+BrowserStringBytes(block.Label)+BrowserStringBytes(block.MediaKey);
        foreach(var member in members)bytes+=2L*member.Length+24L;
        return new(blocks.ToArray(),members.ToArray(),groups,bytes);
    }
    private static long BrowserStringBytes(string? value)=>value is null?0:2L*value.Length+24L;
    private string BrowserLayoutKey(CatalogQuery query)
    {
        var builder=new StringBuilder();void Add(string? value){if(value is null){builder.Append("-1:");return;}builder.Append(value.Length).Append(':').Append(value);}
        Add(SchemaVersion.ToString());Add(Header.Fingerprint);Add(query.Search);Add(query.BankKey);Add(query.EventKey);Add(query.Language);Add(query.Category);Add(query.Codec);Add(query.State?.ToString());Add(query.NameKind?.ToString());Add(query.Dialogue?"1":"0");Add(query.Archive);Add(BrowserSortName(query.Sort));Add(query.Descending?"1":"0");
        if(query.Scope is null)Add(null);else{Add("scope");Add(query.Scope.BankKeys.Length.ToString());foreach(var key in query.Scope.BankKeys.Order(StringComparer.Ordinal))Add(key);Add(query.Scope.EventKeys.Length.ToString());foreach(var key in query.Scope.EventKeys.Order(StringComparer.Ordinal))Add(key);}
        return builder.ToString();
    }
    public IReadOnlyList<MediaRow> ResolveGroupMembers(CatalogQuery query,string groupKey,CancellationToken token=default)
    {
        var all=new List<MediaRow>();foreach(var batch in EnumerateGroupMembers(query,groupKey,500,token))all.AddRange(batch);return all;
    }
    public IEnumerable<IReadOnlyList<MediaRow>> EnumerateGroupMembers(CatalogQuery query,string groupKey,int batchSize=500,CancellationToken token=default)
    {
        batchSize=Math.Clamp(batchSize,1,500);var offset=0;
        while(true)
        {
            var batch=Execute(token,command=>
            {
                var where=BuildWhere(command,query,token);command.Parameters.AddWithValue("$groupKey",groupKey);command.Parameters.AddWithValue("$memberLimit",batchSize);command.Parameters.AddWithValue("$memberOffset",offset);
                command.CommandText="SELECT "+string.Join(',',RowColumns.Split(',').Select(column=>"m."+column))+" FROM media m JOIN media_groups g ON g.media=m.key"+(where.Length==0?" WHERE ":where+" AND ")+"g.group_key=$groupKey ORDER BY "+BrowserOrder(query)+" LIMIT $memberLimit OFFSET $memberOffset";
                using var reader=command.ExecuteReader();var rows=new List<MediaRow>();while(reader.Read()){token.ThrowIfCancellationRequested();rows.Add(ReadMediaRow(reader,0));}return (IReadOnlyList<MediaRow>)rows;
            });
            if(batch.Count==0)yield break;yield return batch;if(batch.Count<batchSize)yield break;offset+=batch.Count;
        }
    }
    public IReadOnlyList<MediaEntry> ReadPage(CatalogQuery query,CancellationToken token=default)=>Execute(token,command=>{command.CommandText=PageSql(command,query,"json",token);using var reader=command.ExecuteReader();var result=new List<MediaEntry>();while(reader.Read()){token.ThrowIfCancellationRequested();result.Add(JsonSerializer.Deserialize<MediaEntry>(reader.GetString(0))!);}return (IReadOnlyList<MediaEntry>)result;});
    public CatalogPage Query(CatalogQuery query,CancellationToken token=default)=>new(ReadPage(query,token),Count(query,token));
    public IEnumerable<MediaEntry> Enumerate(CatalogQuery query,CancellationToken token=default){var total=Count(query,token);for(var offset=0;offset<total;offset+=1000)foreach(var entry in ReadPage(query with{Offset=offset,Limit=1000},token)){token.ThrowIfCancellationRequested();yield return entry;}}
    public MediaEntry? GetMedia(string key,CancellationToken token=default)=>ReadOne<MediaEntry>("media",key,token);
    public AudioEvent? GetEvent(string key,CancellationToken token=default)=>ReadOne<AudioEvent>("events",key,token);
    private T? ReadOne<T>(string table,string key,CancellationToken token)=>Execute(token,command=>{command.CommandText=$"SELECT json FROM {table} WHERE key=$key";command.Parameters.AddWithValue("$key",key);return command.ExecuteScalar() is string json?JsonSerializer.Deserialize<T>(json):default;});
    public IReadOnlyList<BankInfo> GetBanks(CancellationToken token=default)=>Execute(token,command=>{command.CommandText="SELECT summary FROM banks";using var reader=command.ExecuteReader();var result=new List<BankInfo>();while(reader.Read()){token.ThrowIfCancellationRequested();result.Add(JsonSerializer.Deserialize<BankInfo>(reader.GetString(0))!);}return (IReadOnlyList<BankInfo>)result;});
    public IReadOnlyList<AudioEvent> GetEvents(string? bankKey=null,CancellationToken token=default)=>Execute(token,command=>{command.CommandText="SELECT json FROM events"+(bankKey is null?"":" WHERE bank=$bank")+" ORDER BY name,key";if(bankKey is not null)command.Parameters.AddWithValue("$bank",bankKey);using var reader=command.ExecuteReader();var result=new List<AudioEvent>();while(reader.Read()){token.ThrowIfCancellationRequested();result.Add(JsonSerializer.Deserialize<AudioEvent>(reader.GetString(0))!);}return (IReadOnlyList<AudioEvent>)result;});
    public IReadOnlyList<AudioEvent> GetEventSummaries(CancellationToken token=default)=>Execute(token,command=>{command.CommandText="SELECT key,bank,id,name,dialogue,behavior,link FROM events";using var reader=command.ExecuteReader();var result=new List<AudioEvent>();while(reader.Read()){token.ThrowIfCancellationRequested();result.Add(new(){Key=reader.GetString(0),BankKey=reader.GetString(1),Id=(uint)reader.GetInt64(2),Name=reader.GetString(3),IsDialogue=reader.GetInt32(4)!=0,Behavior=(EventBehavior)reader.GetInt32(5),LinkStatus=(EventLinkStatus)reader.GetInt32(6)});}return (IReadOnlyList<AudioEvent>)result;});
    public BankInfo? GetBankGraph(string bankKey,CancellationToken token=default)
    {
        lock(cacheGate){if(graphCache.TryGetValue(bankKey,out var cached)){graphCache[bankKey]=(cached.Bank,++access);return cached.Bank;}}
        var bank=Execute(token,command=>{command.CommandText="SELECT graph FROM banks WHERE key=$key";command.Parameters.AddWithValue("$key",bankKey);if(command.ExecuteScalar() is not byte[] bytes)return null;using var stream=new MemoryStream(bytes);using var compressed=new BrotliStream(stream,CompressionMode.Decompress);return JsonSerializer.Deserialize<BankInfo>(compressed);});
        if(bank is not null){lock(cacheGate){while(graphCache.Count>=4)graphCache.Remove(graphCache.MinBy(pair=>pair.Value.Access).Key);graphCache[bankKey]=(bank,++access);}}return bank;
    }
    public AudioCatalog CreateSummaryCatalog(CancellationToken token=default)=>new(){SourcePath=Header.SourcePath,Fingerprint=Header.Fingerprint,IndexedResourceCount=Header.ResourceCount,Banks=GetBanks(token).ToList(),Events=GetEventSummaries(token).ToList(),Diagnostics=Header.Diagnostics.ToList()};
    public AudioCatalog CreateOperationCatalog(IEnumerable<string>? mediaKeys=null,IEnumerable<string>? bankKeys=null,IEnumerable<string>? eventKeys=null,CancellationToken token=default)
    {
        var result=new AudioCatalog{SourcePath=Header.SourcePath,Fingerprint=Header.Fingerprint,IndexedResourceCount=Header.ResourceCount};
        var summaries=GetBanks(token).ToDictionary(bank=>bank.Key);var events=new Dictionary<string,AudioEvent>();var needed=new Queue<string>(eventKeys??[]);
        while(needed.TryDequeue(out var key)){token.ThrowIfCancellationRequested();if(events.ContainsKey(key))continue;var entry=GetEvent(key,token);if(entry is null)continue;events.Add(key,entry);foreach(var related in entry.RelatedPlayEventKeys)needed.Enqueue(related);}
        result.Events.AddRange(events.Values);
        var loaded=new Dictionary<string,BankInfo>();var pending=new Queue<string>((bankKeys??[]).Concat(events.Values.Select(entry=>entry.BankKey)).Where(key=>key.Length>0));
        while(pending.TryDequeue(out var key))
        {
            token.ThrowIfCancellationRequested();if(loaded.ContainsKey(key))continue;var bank=GetBankGraph(key,token);if(bank is null)continue;loaded.Add(key,bank);
            foreach(var targetBank in bank.Objects.Values.Select(node=>node.TargetBankId).OfType<uint>().Where(id=>id>0&&id!=bank.Id).Distinct())
                foreach(var other in summaries.Values.Where(other=>other.Id==targetBank))pending.Enqueue(other.Key);
            var missingIds = bank.Objects.Values.SelectMany(node => node.Children.Concat(node.TargetId is { } target ? new[] { target } : []))
                .Where(id => !bank.Objects.ContainsKey(id))
                .Concat(bank.Objects.Values.SelectMany(node => node.Actions).Where(id => !bank.Objects.TryGetValue(id, out var local) || local.Type != 3))
                .Distinct().ToArray();
            foreach(var batch in missingIds.Chunk(500))
            {
                var owners=Execute(token,command=>{var parameters=batch.Select((id,index)=>{var parameter="$id"+index;command.Parameters.AddWithValue(parameter,(long)id);return parameter;}).ToArray();command.CommandText="SELECT DISTINCT bank FROM object_owners WHERE id IN("+string.Join(',',parameters)+")";using var reader=command.ExecuteReader();var keys=new List<string>();while(reader.Read())keys.Add(reader.GetString(0));return keys;});foreach(var owner in owners)pending.Enqueue(owner);
            }
        }
        result.Banks.AddRange(loaded.Values);
        var keysToLoad=(mediaKeys??[]).Concat(events.Values.SelectMany(entry=>entry.MediaKeys)).Distinct().ToArray();
        foreach(var batch in keysToLoad.Chunk(500))result.Media.AddRange(Execute(token,command=>{var parameters=batch.Select((key,index)=>{var parameter="$key"+index;command.Parameters.AddWithValue(parameter,key);return parameter;}).ToArray();command.CommandText="SELECT json FROM media WHERE key IN("+string.Join(',',parameters)+")";using var reader=command.ExecuteReader();var media=new List<MediaEntry>();while(reader.Read()){token.ThrowIfCancellationRequested();media.Add(JsonSerializer.Deserialize<MediaEntry>(reader.GetString(0))!);}return media;}));
        return result;
    }
    public List<string> Facets(string column)
    {
        if(column is not("language" or "category" or "codec"))throw new ArgumentException("Unknown facet.",nameof(column));return Execute(default,command=>{command.CommandText=$"SELECT DISTINCT {column} FROM media ORDER BY {column}";using var reader=command.ExecuteReader();var result=new List<string>();while(reader.Read())result.Add(reader.GetString(0));return result;});
    }
    private T Execute<T>(CancellationToken token,Func<SqliteCommand,T> action)
    {
        token.ThrowIfCancellationRequested();using var connection=Open();using var interrupt=token.Register(()=>{if(connection.Handle is {} handle)SQLitePCL.raw.sqlite3_interrupt(handle);});using var command=connection.CreateCommand();try{var result=action(command);token.ThrowIfCancellationRequested();return result;}catch(SqliteException)when(token.IsCancellationRequested){throw new OperationCanceledException(token);}
    }
    private static string BuildWhere(SqliteCommand command,CatalogQuery query,CancellationToken token=default)
    {
        var clauses=new List<string>();void Filter(string expression,string key,object? value){if(value is null||value is string text&&text.Length==0)return;clauses.Add(expression);command.Parameters.AddWithValue(key,value);}
        Filter("bank=$bank","$bank",query.BankKey);Filter("source=$source","$source",query.Archive);Filter("key IN(SELECT media FROM event_media WHERE event=$event)","$event",query.EventKey);Filter("language=$language","$language",query.Language);Filter("category=$category","$category",query.Category);Filter("codec=$codec","$codec",query.Codec);Filter("state=$state","$state",query.State is {} state?(int)state:null);Filter("name_kind=$kind","$kind",query.NameKind is {} kind?(int)kind:null);if(query.Dialogue)clauses.Add("category='Dialogue'");
        if(query.Scope is { } scope)
        {
            PrepareScope(command,scope,token);
            clauses.Add("(bank IN(SELECT key FROM temp.catalog_scope_banks) OR key IN(SELECT media FROM event_media WHERE event IN(SELECT key FROM temp.catalog_scope_events)))");
        }
        var words=query.Search.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(word=>word.ToLowerInvariant()).Select(word=>word.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?word[2..]:word).ToArray();
        var indexed=words.Where(word=>word.EnumerateRunes().Count()>=3).Select(word=>"\""+word.Replace("\"","\"\"")+"\"").ToArray();
        if(indexed.Length>0)Filter("rid IN(SELECT rowid FROM media_search WHERE media_search MATCH $match)","$match",string.Join(" AND ",indexed));
        for(var i=0;i<words.Length;i++)Filter($"search LIKE $search{i} ESCAPE '\\'","$search"+i,"%"+words[i].Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%");
        return clauses.Count==0?"":" WHERE "+string.Join(" AND ",clauses);
    }
    private static void PrepareScope(SqliteCommand command,CatalogScope scope,CancellationToken token)
    {
        using(var create=command.Connection!.CreateCommand()){create.CommandText="CREATE TEMP TABLE catalog_scope_banks(key TEXT PRIMARY KEY); CREATE TEMP TABLE catalog_scope_events(key TEXT PRIMARY KEY);";create.ExecuteNonQuery();}
        FillTempKeys(command.Connection!,"catalog_scope_banks",scope.BankKeys,token);
        FillTempKeys(command.Connection!,"catalog_scope_events",scope.EventKeys,token);
    }
    private static void FillTempKeys(SqliteConnection connection,string table,IEnumerable<string> keys,CancellationToken token)
    {
        foreach(var batch in keys.Chunk(500))
        {
            token.ThrowIfCancellationRequested();using var insert=connection.CreateCommand();
            var values=batch.Select((_,index)=>"($key"+index+")").ToArray();insert.CommandText="INSERT OR IGNORE INTO temp."+table+" VALUES "+string.Join(',',values);
            for(var index=0;index<batch.Length;index++)insert.Parameters.AddWithValue("$key"+index,batch[index]);
            insert.ExecuteNonQuery();
        }
    }
    private sealed record BrowserLayout(BrowserLayoutBlock[] Blocks,string[] MemberKeys,Dictionary<string,BrowserLayoutBlock> Groups,long EstimatedBytes);
    private sealed record BrowserLayoutBlock(string? GroupKey,string Label,string? MediaKey,int MemberStart,int MemberCount);
    private sealed record BrowserDescriptor(BrowserRowKind Kind,string Key,string? GroupKey,string Label,int Count);
    private sealed class BrowserLayoutBuilder(string? groupKey,string label,string? firstKey=null)
    {
        public string? GroupKey { get; }=groupKey;
        public string Label { get; }=label;
        public List<string> Members { get; }=firstKey is null?[]:[firstKey];
    }
    public void Dispose(){lock(cacheGate){graphCache.Clear();browserLayoutCache.Clear();}header=null;}
}
