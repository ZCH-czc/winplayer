using Auralis.Platform.Abstractions;

namespace Auralis.Sample.Pages;

public sealed class Plugin : IAuralisPlatformPlugin
{
#if EXPANDED_PAGES
    internal static readonly Version Version = new(2,0,0);
#else
    internal static readonly Version Version = new(1,0,0);
#endif
    public PlatformPluginDescriptor Descriptor { get; } = new("sample.pages","Declarative page sample",Version,1,1);
    public ValueTask<PlatformResult<IReadOnlyList<IPlatformProvider>>> CreateProvidersAsync(PlatformHostContext context,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PlatformResult<IReadOnlyList<IPlatformProvider>>.Success([new Provider(context.Settings)]));
    }
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

public sealed class Provider(IPlatformSettings settings) : IPlatformProvider, IPlatformGlobalPagesCapability, IPlatformPagesCapability, ITrackSearchCapability, IStreamResolutionCapability, ICommentsCapability, ICommentRepliesCapability
{
    public PlatformProviderDescriptor Descriptor { get; } = new("sample.pages","Page sample",Plugin.Version,
#if EXPANDED_PAGES
        [PlatformCapabilityKind.StreamResolution,PlatformCapabilityKind.GlobalPages,PlatformCapabilityKind.Pages,PlatformCapabilityKind.TrackSearch,PlatformCapabilityKind.Comments,PlatformCapabilityKind.CommentReplies]);
#else
        [PlatformCapabilityKind.Pages,PlatformCapabilityKind.TrackSearch]);
#endif
    public ValueTask<PlatformResult<PlatformUnit>> InitializeAsync(CancellationToken token)=>ValueTask.FromResult(PlatformResult<PlatformUnit>.Success(PlatformUnit.Value));
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    public Task<PlatformResult<PlatformPage<PlatformTrack>>> SearchTracksAsync(PlatformSearchRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(PlatformResult<PlatformPage<PlatformTrack>>.Success(new([new()
        { Id=new("sample.pages","demo"),Title="Original page demonstration" }])));
    }
    public Task<PlatformResult<PlatformPageDocument>> ReadGlobalPageAsync(PlatformGlobalPageReadRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if EXPANDED_PAGES
        if(request.ProviderId!="sample.pages" || request.Route is not ("hub" or "notes" or "archive" or "search") || request.State is not null)
            return Fail("Invalid global context");
        if(request.Route=="search")
            return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new(){
                Version=5,Title="Plugin-only search",Cards=[new(){Id="result",
                    Text=request.InputValues["query"]+"/"+request.InputValues["kind"],
                    Actions=[new("View catalogue","discography") { Target=new("discography",new("sample.pages","demo"),"media") }]}]
            }));
        return Task.FromResult(PlatformResult<PlatformPageDocument>.Success(new(){
            Version=4,Title="Sample space — plugin-owned navigation",
            Query=request.Route=="hub"?new(new("Search","search"),[
                new(){Key="query",Label="Search the demo",MinLength=1},
                new(){Key="kind",Label="Collection",Kind="choice",Value="all",Options=[new("All","all"),new("Albums","albums")]}
            ]):null,
            Tabs=[new(new("Overview","hub"),request.Route=="hub"),new(new("Notes","notes"),request.Route=="notes"),
                new(new("Archive","archive"),request.Route=="archive")],
            Cards=[new(){Id=request.Route,Title=request.Route=="hub"?"Welcome":request.Route=="notes"?"Release notes":"Archive",
                Text="This entry and these tabs were added only in plugin v2. No media context or HTTP."}]
        }));
#else
        return Fail("No global page in v1");
#endif
    }
    public Task<PlatformResult<PlatformPageDocument>> ReadPageAsync(PlatformPageReadRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!request.MediaId.IsForProvider("sample.pages"))return Fail("Invalid context");
        var page=new PlatformPageDocument {Title="Page sample v1",Layout="list",Cards=[new(){Title="Biography",Text="Original public demonstration. No music, accounts or network."}]};
#if EXPANDED_PAGES
        if(request.Route=="discography" && request.State is null or "second")
            page=new() {Version=6,Title="Discography — added only by plugin v2",Cards=[
                new(){Id="record-one",Media=new() { Id=new("sample.pages","quiet-geometry"), Title="Quiet Geometry", Duration=TimeSpan.FromSeconds(204), Availability=PlatformTrackAvailability.Available },Title="Quiet Geometry",Text="An imaginary record.",Images=[new Uri("https://example.test/art-one"),new Uri("https://example.test/art-two")],Discussion=new("sample.pages","discussion-one"),CommentCount=2},
                new(){Id=request.State is null?"record-two":"record-three",Title="Evening Colors",Text="Original demonstration."}],
                Next=request.State is null?new("More records","discography","second"):null};
        else if(request.Route=="profile" && request.State is null)
            page=page with {Title="Page sample v2",Actions=[new("Explore the new page","discography")]};
        else return Fail("Invalid navigation");
#else
        if(request.Route!="profile" || request.State is not null)return Fail("Page is not in v1");
#endif
        return ApplyPreferencesAsync(page, token);
    }
    private async Task<PlatformResult<PlatformPageDocument>> ApplyPreferencesAsync(PlatformPageDocument page, CancellationToken token)
    {
        // The host only interprets availability and stores values. The plugin owns their meaning.
        if (await settings.GetAsync("direction", token) == "descending")
            page = page with { Cards = page.Cards.Reverse().ToArray() };
        return PlatformResult<PlatformPageDocument>.Success(page);
    }
    public Task<PlatformResult<IReadOnlyList<PlatformAudioQuality>>> GetAvailableQualitiesAsync(PlatformEntityId id,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(PlatformResult<IReadOnlyList<PlatformAudioQuality>>.Success([new("demo","Demo")]));
    }
    // Returns a test-only lease; the upgrade proof does not download or decode this URI.
    public Task<PlatformResult<PlatformStreamLease>> AcquireStreamAsync(PlatformPlaybackRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(request.TrackId!=new PlatformEntityId("sample.pages","quiet-geometry"))
            return Task.FromResult(PlatformResult<PlatformStreamLease>.Failure(PlatformErrorCode.NotFound,"No sample media"));
        return Task.FromResult(PlatformResult<PlatformStreamLease>.Success(new(new Uri("https://example.test/original-demo.mp3"),
            DateTimeOffset.UtcNow.AddMinutes(1),"audio/mpeg",new("demo","Demo"))));
    }
    private static Task<PlatformResult<PlatformPageDocument>> Fail(string text)=>Task.FromResult(PlatformResult<PlatformPageDocument>.Failure(PlatformErrorCode.InvalidRequest,text));
    public Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentsAsync(PlatformCommentsRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(request.EntityId!=new PlatformEntityId("sample.pages","discussion-one"))
            return Task.FromResult(PlatformResult<PlatformPage<PlatformComment>>.Failure(PlatformErrorCode.InvalidRequest,"Invalid discussion"));
        return Task.FromResult(PlatformResult<PlatformPage<PlatformComment>>.Success(new([new(){Id=new("sample.pages","root-one"),Author=new(){DisplayName="Original reader"},Text="A sample comment",ReplyCount=2,PublishedAt=DateTimeOffset.UnixEpoch}])));
    }
    public Task<PlatformResult<PlatformPage<PlatformComment>>> GetCommentRepliesAsync(PlatformEntityId entity,PlatformEntityId root,PlatformPageRequest page,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(entity!=new PlatformEntityId("sample.pages","discussion-one") || root!=new PlatformEntityId("sample.pages","root-one") || page.Cursor is not (null or "second"))
            return Task.FromResult(PlatformResult<PlatformPage<PlatformComment>>.Failure(PlatformErrorCode.InvalidRequest,"Invalid thread"));
        return Task.FromResult(PlatformResult<PlatformPage<PlatformComment>>.Success(new([new(){Id=new("sample.pages",page.Cursor is null?"reply-one":"reply-two"),Author=new(){DisplayName="Reply author"},Text="Original sample reply",PublishedAt=DateTimeOffset.UnixEpoch}],page.Cursor is null?"second":null)));
    }
}
