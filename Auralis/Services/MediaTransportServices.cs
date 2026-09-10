using System.IO;
using System.Net.Http;
using Auralis.MediaTransport;
using Auralis.MediaTransport.Host;

namespace Auralis.Services;

/// <summary>Only concrete transport/budget composition. Freeze explicitly approved startup selection;
/// preserve independent bundled fallback, deferred activation and existing shared admission budget.</summary>
internal static class MediaTransportServices
{
    private static readonly IMediaTransferBudget SharedBudget = new MediaTransferBudget();
    internal sealed record Runtime(string StorageRoot, MediaTransportInstallationStore? Store, MediaTransportInstallationState InitialState,
        MediaTransportComponentComposition Composition);
    private static readonly Lazy<Runtime> Current = new(() => CreateRuntime(StorageRoot));
    internal static Runtime CurrentRuntime => Current.Value;
    internal static string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auralis", "TransportComponents");
    internal static MediaTransportInstallationStore Installations => Current.Value.Store ?? throw new MediaTransportInstallationException(MediaTransportInstallationIssue.StorageFailure);
    internal static MediaTransportInstallationState InitialState => Current.Value.InitialState;
    internal static MediaTransportComponentComposition Composition => Current.Value.Composition;
    internal static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Auralis","StreamingCache");
    internal static IMediaTransportSession CreateDefault() => Composition.CreateDeferred(new(CacheDirectory,SharedBudget));

    // Explicit composition input also permits a native management fixture to use its own directory.
    // It does not override process-wide app paths, credentials, or the production Lazy instance.
    internal static Runtime CreateRuntime(string storageRoot)
    {
        var bundled = new MediaTransportRegistration(HttpMediaTransportFactory.Metadata, true, static () => new HttpMediaTransportFactory());
        try
        {
            var store = new MediaTransportInstallationStore(storageRoot, MediaTransportCapabilities.Full);
            var initial = store.ReadState(); var plan = store.ReadStartupPlan();
            return new(storageRoot, store, initial, new(plan, bundled, MediaTransportCapabilities.Full));
        }
        catch
        {
            return new(storageRoot, null, new(MediaTransportInstallationIssue.StorageFailure, [], null),
                new(new(MediaTransportInstallationIssue.StorageFailure, [], null, []), bundled, MediaTransportCapabilities.Full));
        }
    }

    // Explicit fixture injection: never touches default cache or production shared admission state.
    internal static IMediaTransportSession CreateIsolated(HttpMessageHandler handler,string cacheDirectory) =>
        new HttpMediaTransportSession(cacheDirectory,handler,budget:new MediaTransferBudget());
}
