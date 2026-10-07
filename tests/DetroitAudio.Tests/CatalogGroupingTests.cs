using DetroitAudio.Core;
using DetroitAudio.Indexing;
using Xunit;

namespace DetroitAudio.Tests;

public sealed class CatalogGroupingTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"catalog-grouping-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public void ScopeUsesDistinctBankOrEventUnionAndStillAppliesOtherFilters()
    {
        using var store=new CatalogStore(directory);
        var catalog=new AudioCatalog
        {
            Media=[Entry("bank",1,"bank-a","Alpha","BankClip"),Entry("event",2,"bank-b","Beta","EventClip"),Entry("other",3,"bank-c","Gamma","EventClip")],
            Events=[new(){Key="event-a",BankKey="bank-c",Name="Named",Behavior=EventBehavior.Playback,MediaKeys=["event","event"]}]
        };
        store.Save(catalog,null,default);
        var scope=new CatalogScope(["bank-a","bank-a"],["event-a","event-a"]);
        Assert.Equal(new[]{"bank","event"},store.ReadRows(new(Scope:scope)).Select(row=>row.Key));
        Assert.Equal(2,store.Count(new(Scope:scope)));
        Assert.Single(store.ReadRows(new(Scope:scope,Search:"BankClip")));
        var thousands=new CatalogScope(Enumerable.Range(0,3000).Select(i=>"missing-"+i).Append("bank-a"));
        Assert.Equal("bank",Assert.Single(store.ReadRows(new(Scope:thousands))).Key);
    }

    [Fact]
    public void GroupsAreFormedAfterFilteringAndExpansionPreservesLanguageAndSortOffsets()
    {
        using var store=new CatalogStore(directory);
        store.Save(new(){Media=[Entry("a",1,"bank","Bank","Bird",language:"EN"),Entry("b",2,"bank","Bank","Bird",language:"FR"),Entry("z",3,"bank","Bank","Solo")]},null,default);

        var collapsed=store.ReadBrowserRows(new(),true,null,0,500);
        Assert.Equal(2,store.CountBrowserRows(new(),true));
        Assert.Equal(BrowserRowKind.GroupHeader,collapsed[0].Kind);
        Assert.Equal("Bird",collapsed[0].Label);Assert.Equal(2,collapsed[0].Count);
        Assert.Equal("z",collapsed[1].Media!.Key);

        var expanded=new HashSet<string>([collapsed[0].GroupKey!],StringComparer.Ordinal);
        Assert.Equal(4,store.CountBrowserRows(new(),true,expanded));
        var rows=store.ReadBrowserRows(new(),true,expanded,0,500);
        Assert.Equal(new[]{BrowserRowKind.GroupHeader,BrowserRowKind.Media,BrowserRowKind.Media,BrowserRowKind.Media},rows.Select(row=>row.Kind));
        Assert.Equal(new[]{"EN","FR"},rows.Skip(1).Take(2).Select(row=>row.Media!.Language));
        Assert.Equal(expanded.Single(),rows[1].GroupKey);Assert.Equal(expanded.Single(),rows[2].GroupKey);
        Assert.Equal("b",store.ReadBrowserRows(new(),true,expanded,2,1).Single().Media!.Key);
        Assert.Equal(new[]{"a","b"},store.ResolveGroupMembers(new(),expanded.Single()).Select(row=>row.Key));
        Assert.Equal(2,store.EnumerateGroupMembers(new(),expanded.Single(),1).Count());

        Assert.Single(store.ReadBrowserRows(new(Language:"EN"),true,null,0,10));
        Assert.Equal(BrowserRowKind.Media,store.ReadBrowserRows(new(Language:"EN"),true,null,0,10).Single().Kind);
    }

    [Fact]
    public void MeaningfulLinkedStateEventOutranksPlaybackNameAndUnhelpfulNamesStaySingle()
    {
        using var store=new CatalogStore(directory);
        var catalog=new AudioCatalog
        {
            Media=[Entry("one",1,"bank","Bank","1001"),Entry("two",2,"bank","Bank","Bank"),Entry("three",3,"bank","Bank","1002")],
            Events=[
                new(){Key="play",BankKey="bank",Name="z_play_event",Behavior=EventBehavior.Playback,MediaKeys=["one","two"]},
                new(){Key="state",BankKey="bank",Name="a_state_event",Behavior=EventBehavior.StateChange,RelatedPlayEventKeys=["play"],MediaKeys=["one","two"]}]
        };
        store.Save(catalog,null,default);
        var collapsed=store.ReadBrowserRows(new(),true,null,0,100);
        Assert.Equal(2,collapsed.Count);
        var group=Assert.Single(collapsed,row=>row.Kind==BrowserRowKind.GroupHeader);
        Assert.Equal("a_state_event",group.Label);
        Assert.Equal(2,group.Count);
        Assert.Equal("three",Assert.Single(collapsed,row=>row.Kind==BrowserRowKind.Media).Media!.Key);
    }

    [Fact]
    public void SharedPlaybackEventDoesNotLeakStateLabelsAcrossDisjointBranches()
    {
        using var store=new CatalogStore(directory);
        var catalog=new AudioCatalog
        {
            Media=[Entry("alpha-1",1,"bank","Bank","1001"),Entry("alpha-2",2,"bank","Bank","1002"),Entry("beta-1",3,"bank","Bank","1003"),Entry("beta-2",4,"bank","Bank","1004")],
            Events=[
                new(){Key="play",BankKey="bank",Name="shared_play",Behavior=EventBehavior.Playback,MediaKeys=["alpha-1","alpha-2","beta-1","beta-2"]},
                new(){Key="state-alpha",BankKey="bank",Name="state_alpha",Behavior=EventBehavior.StateChange,RelatedPlayEventKeys=["play"],MediaKeys=["alpha-1","alpha-2"]},
                new(){Key="state-beta",BankKey="bank",Name="state_beta",Behavior=EventBehavior.StateChange,RelatedPlayEventKeys=["play"],MediaKeys=["beta-1","beta-2"]}]
        };
        store.Save(catalog,null,default);

        var rows=store.ReadBrowserRows(new(),true,null,0,100);
        Assert.Equal(2,rows.Count(row=>row.Kind==BrowserRowKind.GroupHeader));
        var alpha=Assert.Single(rows,row=>row.Kind==BrowserRowKind.GroupHeader&&row.Label=="state_alpha");
        var beta=Assert.Single(rows,row=>row.Kind==BrowserRowKind.GroupHeader&&row.Label=="state_beta");
        Assert.Equal(2,alpha.Count);Assert.Equal(2,beta.Count);
        Assert.Equal(new[]{"alpha-1","alpha-2"},store.ResolveGroupMembers(new(),alpha.GroupKey!).Select(row=>row.Key));
        Assert.Equal(new[]{"beta-1","beta-2"},store.ResolveGroupMembers(new(),beta.GroupKey!).Select(row=>row.Key));
    }

    [Fact]
    public void ExpandedGroupsStayContiguousWhenTheirMembersInterleaveInSortOrder()
    {
        using var store=new CatalogStore(directory);
        store.Save(new(){Media=[Entry("a0",1,"bank","Bank","Alpha"),Entry("b1",2,"bank","Bank","Beta"),Entry("a2",3,"bank","Bank","Alpha"),Entry("b3",4,"bank","Bank","Beta")]},null,default);
        var collapsed=store.ReadBrowserRows(new(Sort:"ID"),true,null,0,100);
        var expanded=new HashSet<string>(collapsed.Where(row=>row.Kind==BrowserRowKind.GroupHeader).Select(row=>row.GroupKey!),StringComparer.Ordinal);
        var rows=store.ReadBrowserRows(new(Sort:"ID"),true,expanded,0,100);
        Assert.Equal(new[]{"H:Alpha","M:a0","M:a2","H:Beta","M:b1","M:b3"},rows.Select(row=>row.Kind==BrowserRowKind.GroupHeader?"H:"+row.Label:"M:"+row.Media!.Key));
    }

    [Fact]
    public void BrowserReadsHonorCancellation()
    {
        using var store=new CatalogStore(directory);store.Save(new(){Media=[Entry("one",1,"bank","Bank","Bird")]},null,default);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>store.CountBrowserRows(new(),true,null,cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(()=>store.ReadBrowserRows(new(),true,null,0,50,cancelled.Token));
    }

    [Fact]
    public void FilteredLayoutsAreBoundedAndCancelledOrReplacedLayoutsAreNotPublished()
    {
        using var store=new CatalogStore(directory);
        store.Save(new(){Fingerprint="first",Media=Enumerable.Range(0,10).Select(index=>Entry("media-"+index,(uint)index,"bank","Bank","Name-"+index,"L"+index)).ToList()},null,default);
        for(var index=0;index<10;index++)store.CountBrowserRows(new(Language:"L"+index),true);
        Assert.Equal(8,store.CachedBrowserLayoutCount);

        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>store.CountBrowserRows(new(Language:"cancelled"),true,null,cancelled.Token));
        Assert.Equal(8,store.CachedBrowserLayoutCount);

        store.Save(new(){Fingerprint="replacement",Media=[Entry("replacement",1,"bank","Bank","Replacement")]},null,default);
        Assert.Equal(0,store.CachedBrowserLayoutCount);
    }

    private static MediaEntry Entry(string key,uint id,string bank,string bankName,string name,string language="SFX")=>new()
    {
        Key=key,Id=id,BankKey=bank,BankName=bankName,Name=name,Language=language,Slice=new("generated.dat",0,4),
        State=MediaState.CompleteEmbedded,Completeness=MediaCompleteness.Complete,ContainerValidity=ContainerValidity.Valid,
        Availability=SourceAvailability.Available,IsRiff=true,Codec="PCM",SampleRate=48000,Channels=2
    };
    public void Dispose(){try{if(Directory.Exists(directory))Directory.Delete(directory,true);}catch(IOException){}}
}
