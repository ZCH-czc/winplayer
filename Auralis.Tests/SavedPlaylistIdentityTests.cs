using Auralis.Services;

internal static class SavedPlaylistIdentityTests
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var sample = new SavedPlaylistEntry("entry", null, "publisher.example-source", "track:part-2", "Title", "Artist", "Album", 120, "video:part-2");
        foreach (var id in new[] { "publisher.example-source", "user_server2", "NEW.Source", "0", new string('a',64) })
            Check(SavedPlaylistStore.IsValid(sample with { ProviderId=id }), "Any valid scoped provider ID can be saved");
        foreach (var id in new string?[] { null, "", " ", " leading", "trailing ", ".prefix", "-prefix", "_prefix", "中文", "x/y", "x\\y", "https://example.test", "x?token=secret", "x#hash", "x@host", "x&key", "x\n", new string('a',65) })
            Check(!SavedPlaylistStore.IsValid(sample with { ProviderId=id }), "Provider syntax excludes paths, URLs and unbounded input");
        foreach (var id in new[] { "https://example.test/?secret=fixture", "relative/path", "C:\\music", "item?token=fixture", "item#token", "item@host", "item&token", "item\n" })
            Check(!SavedPlaylistStore.IsValid(sample with { EntityId=id }) && !SavedPlaylistStore.IsValid(sample with { VideoId=id }), "Opaque references cannot become secret-bearing media addresses");
        Check(!SavedPlaylistStore.IsValid(sample with { LocalId="local" }), "A reference cannot be both local and online");
        var root=Path.Combine(Path.GetTempPath(), "auralis-generic-saved-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"saved-playlists.json");
            var store=new SavedPlaylistStore(path);
            await store.ChangeAsync("create",name:"Mixed");
            var id=(await store.LoadAsync()).Single().Id;
            await store.ChangeAsync("add",id,entry:sample);
            await store.ChangeAsync("add",id,entry:sample with {Id="duplicate",ProviderId=sample.ProviderId!.ToUpperInvariant()});
            await store.ChangeAsync("add",id,entry:sample with {Id="another-provider",ProviderId="another.publisher"});
            await store.ChangeAsync("add",id,entry:sample with {Id="case-sensitive-track",EntityId="TRACK:part-2"});
            await store.ChangeAsync("add",id,entry:new("local","local-id",null,null,"Local","Artist","Album",10,null));
            var before=await File.ReadAllBytesAsync(path);
            var loaded=(await new SavedPlaylistStore(path).LoadAsync()).Single();
            Check(loaded.Entries.Count==4,"Same provider ignoring case deduplicates; different provider or exact track case remains distinct");
            Check(loaded.Entries[0]==sample,"Reload preserves opaque source identity, video part and display metadata without plugins");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(before),"Read does not rewrite user data or require migration");
            var fallback=SavedTrackMetadata.Fallback(loaded.Entries[0]);
            Check(fallback.Id.ProviderId==sample.ProviderId && fallback.MusicVideo?.Id.Value==sample.VideoId,"Generic source restores original audio/video identity");
            var enriched=fallback with { ArtworkUrl=new Uri("https://images.example.test/cover") };
            Check(SavedTrackMetadata.Match(fallback.Id,[enriched])==enriched,"Original full metadata can be restored");
            Check(SavedTrackMetadata.Match(fallback.Id,[enriched with {Id=new("other.source",sample.EntityId!)}]) is null,"Cover cannot leak from a different provider");
            try { await store.ChangeAsync("add",id,entry:sample with {ProviderId="invalid/source"}); throw new Exception("Invalid write accepted"); }
            catch(InvalidOperationException) { checks++; }
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(before),"Invalid new entry leaves entire original file intact");
            await store.ChangeAsync("remove",id,entryId:sample.Id);
            Check((await store.LoadAsync()).Single().Entries.Count==3,"An unavailable plugin reference can still be removed");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {checks} generic saved-reference checks; no platform inventory, accounts, network or media files.");
    }
}
