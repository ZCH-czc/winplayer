using Auralis.Platform.Abstractions;

namespace Auralis.Platform.Host.Tests;

internal static class PluginPageReadingTests
{
    internal static void Run()
    {
        var count=0;
        void Check(bool value,string message){count++;if(!value)throw new InvalidOperationException(message);}
        var card=new PlatformPageCard { Id="entry",Title="Original demonstration",Author="Example",
            Open=new("Read","detail","backend-only"),Discussion=new("sample.reading","discussion") };
        var feed=new PlatformPageDocument {Version=7,Title="Reading",Layout="feed",Cards=[card]};
        Check(PlatformPageValidation.IsValid(feed),"V7 chronological feed accepted");
        Check(PlatformPageValidation.IsValid(feed with {Layout="detail"}),"V7 single-item detail accepted");
        for(var v=1;v<=6;v++){
            Check(!PlatformPageValidation.IsValid(feed with {Version=v}),"Old version rejects feed");
            Check(!PlatformPageValidation.IsValid(feed with {Version=v,Layout="cards"}),"Old version rejects primary action");
        }
        Check(!PlatformPageValidation.IsValid(feed with {Layout="detail",Cards=[]}),"Detail requires one item");
        Check(!PlatformPageValidation.IsValid(feed with {Layout="detail",Cards=[card,card with {Id="second"}]}),"No ambiguous detail subject");
        Check(!PlatformPageValidation.IsValid(feed with {Layout="detail",Next=new("More","next")}),"Detail cannot append");
        Check(!PlatformPageValidation.IsValid(feed with {Cards=[card with {Open=new("Bad","../bad")}]}),"Primary action validated");
        Check(!PlatformPageValidation.IsValid(feed with {Cards=[card with {Discussion=default(PlatformEntityId)}]}),"Malformed discussion rejected");
        Check(!PlatformPageValidation.IsValid(feed with {Cards=Enumerable.Range(0,100).Select(i=>card with {Id=i.ToString(),Actions=[new("A","a")]}).ToArray()}),"Primary actions count toward handle budget");
        Check(PlatformPageEntry.IsValidList([new(){Id="read",Label="阅读",LabelEn="Reading",Presentation="page",DocumentVersion=7}]),"V7 page declaration accepted");
        Check(!PlatformPageEntry.IsValidList([new(){Id="read",Label="阅读",LabelEn="Reading",DocumentVersion=7}]),"V7 modal declaration rejected");
        Check(PlatformHostCompatibility.Current.Features.Contains("declarative-pages.v7"),"Host declares reading feature");
        var quote=new PlatformPageQuote {Author="Original",Text="Hello [smile]",Body=[new("Hello "),new("[smile]",new("https://art.example.test/e.png"))],Discussion=new("sample.reading","original")};
        var rich=feed with {Version=8,Cards=[card with {Quote=quote}]};
        Check(PlatformPageValidation.IsValid(rich),"V8 one-level attribution accepted");
        Check(!PlatformPageValidation.IsValid(rich with {Version=7}),"V7 rejects new fields");
        foreach(var invalid in new[] {quote with {Body=[new("different")]},quote with {Status="recursive"},
            quote with {Body=null!},quote with {Body=[new("Hello [smile]",new("http://insecure.test/a"))]},
            quote with {Status="unavailable"},quote with {CommentCount=-1},quote with {Images=Enumerable.Repeat(new Uri("https://art.example.test/a"),10).ToArray()}})
            Check(!PlatformPageValidation.IsValid(rich with {Cards=[card with {Quote=invalid}]}),"Invalid quote rejected");
        Check(PlatformPageValidation.IsValid(rich with {Cards=[card with {Quote=new(){Status="unavailable",Text="Not accessible"}}]}),"Inert unavailable quote accepted");
        Check(!PlatformPageValidation.IsValid(rich with {Cards=[card with {Body=Enumerable.Repeat(new PlatformPageTextRun("x"),129).ToArray(),Text=new string('x',129)}]}),"Run count bounded");
        Check(!PlatformPageValidation.IsValid(rich with {Cards=Enumerable.Range(0,100).Select(i=>card with {Id=i.ToString(),Quote=quote with {AuthorAction=new("Author","profile")}}).ToArray()}),"Quote actions consume shared budget");
        Check(!PlatformPageValidation.IsValid(rich with {Cards=Enumerable.Range(0,5).Select(i=>card with {Id=i.ToString(),Quote=quote with {Text=new string('x',32000),Body=[]}}).ToArray()}),"Quote text consumes page budget");
        Check(PlatformHostCompatibility.Current.Features.Contains("declarative-pages.v8"),"Host declares v8");
        var updates=new PlatformPageUpdates(new("Check","check"),new("Reload","reload"),"backend-revision");
        var live=rich with {Version=9,Navigation=[new(new("All","all"),Selected:true)],Updates=updates};
        Check(PlatformPageValidation.IsValid(live),"V9 filters and visible updates accepted");
        Check(!PlatformPageValidation.IsValid(live with {Version=8}),"Old docs cannot smuggle v9 fields");
        Check(!PlatformPageValidation.IsValid(live with {Navigation=null!}),"Null navigation rejected");
        Check(!PlatformPageValidation.IsValid(live with {Navigation=[new(new("None","all"))]}),"One selected filter required");
        Check(!PlatformPageValidation.IsValid(live with {Navigation=Enumerable.Repeat(live.Navigation[0],25).ToArray()}),"Navigation bounded");
        Check(!PlatformPageValidation.IsValid(live with {Navigation=[new(new("Unsafe","all"),new("file:///secret"),true)]}),"Rail image policy");
        Check(!PlatformPageValidation.IsValid(live with {Layout="detail"}),"Detail cannot opt into polling");
        foreach(var invalid in new[]{updates with {IntervalSeconds=1},updates with {IntervalSeconds=901},updates with {Revision=""},updates with {Revision=new string('x',513)},updates with {Check=new("Write","../command")},updates with {Reload=new("Target","all"){Target=new("creator",new("sample","id"),"creator")}}})
            Check(!PlatformPageValidation.IsValid(live with {Updates=invalid}),"Update constraints");
        Console.WriteLine($"PASS reading contract: {count} checks (v1–v9 compatibility, bounds and explicit read-only navigation).");
    }
}
