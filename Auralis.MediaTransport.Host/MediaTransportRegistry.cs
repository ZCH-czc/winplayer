using System.Runtime.CompilerServices;

namespace Auralis.MediaTransport.Host;

public enum MediaTransportIssue
{
    NotRegistered, Disabled, ApiMismatch, HostTooOld, MissingCapability, ActivationFailed,
    DescriptorMismatch, CreationFailed, ReusedFactory, ReusedSession, SessionDisposeFailed, FactoryDisposeFailed
}
public sealed record MediaTransportRegistration(MediaTransportDescriptor Descriptor, bool Enabled, Func<IMediaTransportFactory> Activate);
public sealed record MediaTransportStatus(MediaTransportDescriptor Descriptor, bool Enabled, MediaTransportIssue? Issue);
public sealed record MediaTransportSelection(IMediaTransportSession Session, string ComponentId, bool UsedFallback, IReadOnlyList<MediaTransportIssue> Issues);
public sealed class MediaTransportComponentException : InvalidOperationException
{
    public IReadOnlyList<MediaTransportIssue> Issues { get; }
    public MediaTransportComponentException(IEnumerable<MediaTransportIssue> issues) : base("Media transport component is unavailable.") =>
        Issues = Array.AsReadOnly(issues.ToArray());
}

/// <summary>Explicit trusted registration only. Metadata inspection does not activate code. Optional
/// managed creation failures are latched for this registry lifetime; bundled fallback is independent,
/// including when IDs match. No disk discovery, approval bypass, native isolation or request-time retry.</summary>
public sealed class MediaTransportRegistry
{
    public const int ApiVersion = 1;
    public static Version HostVersion { get; } = new(0,6,0);
    private readonly Dictionary<string,MediaTransportRegistration> _optional = new(StringComparer.Ordinal);
    private readonly MediaTransportRegistration _bundled;
    private readonly Dictionary<MediaTransportRegistration,MediaTransportIssue> _failures = new(ReferenceEqualityComparer.Instance);
    private readonly object _failureGate = new();
    private static readonly object OwnershipGate = new();
    private static readonly ConditionalWeakTable<IMediaTransportFactory,object> Factories = new();
    private static readonly ConditionalWeakTable<IMediaTransportSession,object> Sessions = new();

    public MediaTransportRegistry(IEnumerable<MediaTransportRegistration> optional, MediaTransportRegistration bundled)
    {
        ArgumentNullException.ThrowIfNull(optional);
        if (!Valid(bundled) || !bundled.Enabled) throw new ArgumentException("Invalid bundled transport.");
        _bundled=bundled;
        foreach(var item in optional.Take(65))
            if(!Valid(item)||_optional.Count==64||!_optional.TryAdd(item.Descriptor.Id,item))
                throw new ArgumentException("Invalid or duplicate transport registration.");
    }
    private static bool Valid(MediaTransportRegistration? item) => item?.Descriptor is { } d && item.Activate is not null &&
        d.Id is {Length: >0 and <=128} && d.Id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '_') &&
        !string.IsNullOrWhiteSpace(d.DisplayName) && d.DisplayName.Length<=80 &&
        d.ComponentVersion is not null && d.MinimumHostVersion is not null && (d.Capabilities & ~MediaTransportCapabilities.Full)==0;
    private static void Validate(MediaTransportCapabilities required)
    {
        if(required==MediaTransportCapabilities.None||(required & ~MediaTransportCapabilities.Full)!=0)
            throw new ArgumentOutOfRangeException(nameof(required));
    }
    private MediaTransportIssue? Check(MediaTransportRegistration item, MediaTransportCapabilities required)
    {
        if(!item.Enabled)return MediaTransportIssue.Disabled;
        if(item.Descriptor.ApiVersion!=ApiVersion)return MediaTransportIssue.ApiMismatch;
        if(item.Descriptor.MinimumHostVersion>HostVersion)return MediaTransportIssue.HostTooOld;
        if((item.Descriptor.Capabilities & required)!=required)return MediaTransportIssue.MissingCapability;
        lock(_failureGate) return _failures.TryGetValue(item,out var issue)?issue:null;
    }
    public IReadOnlyList<MediaTransportStatus> Inspect(MediaTransportCapabilities required)
    {
        Validate(required);
        return Array.AsReadOnly(_optional.Values.Append(_bundled).Select(r=>new MediaTransportStatus(r.Descriptor,r.Enabled,Check(r,required))).ToArray());
    }

    public IMediaTransportSession CreateDeferred(string? preferredId, MediaTransportContext context, MediaTransportCapabilities required)
    {
        ArgumentNullException.ThrowIfNull(context);Validate(required);
        return new DeferredSession(()=>CreateAsync(preferredId,context,required));
    }
    internal static IMediaTransportSession Defer(Func<Task<MediaTransportSelection>> create) => new DeferredSession(create);

    private sealed class DeferredSession(Func<Task<MediaTransportSelection>> create) : IMediaTransportSession
    {
        private readonly object _gate=new();
        private Task<MediaTransportSelection>? _creation;private Task? _close;
        public async Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request,string? identity,CancellationToken token,bool prefetch=false)
        {
            token.ThrowIfCancellationRequested();
            Task<MediaTransportSelection> creation;
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(_close is not null,this);
                creation=_creation??=Task.Run(create);
            }
            var selection=await creation.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock(_gate)ObjectDisposedException.ThrowIf(_close is not null,this);
            return await selection.Session.PrepareAsync(request,identity,token,prefetch).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync()
        {
            lock(_gate)return new(_close??=Task.Run(CloseAsync));
        }
        private async Task CloseAsync()
        {
            Task<MediaTransportSelection>? creation;
            lock(_gate)creation=_creation;
            if(creation is null)return;
            MediaTransportSelection selection;
            try{selection=await creation.ConfigureAwait(false);}
            catch(MediaTransportComponentException){return;} // Failed creation already cleaned owned candidates.
            await selection.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<MediaTransportSelection> CreateAsync(string? preferredId, MediaTransportContext context,
        MediaTransportCapabilities required)
    {
        ArgumentNullException.ThrowIfNull(context);Validate(required);
        var issues=new List<MediaTransportIssue>();
        var attempts=new List<MediaTransportRegistration>();
        if(!string.IsNullOrEmpty(preferredId))
        {
            if(_optional.TryGetValue(preferredId,out var candidate))attempts.Add(candidate);
            else issues.Add(MediaTransportIssue.NotRegistered);
        }
        attempts.Add(_bundled);
        foreach(var registration in attempts)
        {
            if(Check(registration,required) is { } rejected){issues.Add(rejected);continue;}
            IMediaTransportFactory? factory=null;IMediaTransportSession? session=null;
            var ownsFactory=false;var ownsSession=false;
            var issue=MediaTransportIssue.ActivationFailed;
            try
            {
                factory=registration.Activate()??throw new InvalidOperationException();
                issue=MediaTransportIssue.ReusedFactory;
                lock(OwnershipGate)
                {
                    if(Factories.TryGetValue(factory,out _))throw new InvalidOperationException();
                    Factories.Add(factory,new object());ownsFactory=true;
                }
                issue=MediaTransportIssue.DescriptorMismatch;
                if(factory.Descriptor!=registration.Descriptor)throw new InvalidOperationException();
                issue=MediaTransportIssue.CreationFailed;
                session=factory.Create(context)??throw new InvalidOperationException();
                issue=MediaTransportIssue.ReusedSession;
                lock(OwnershipGate)
                {
                    if(Sessions.TryGetValue(session,out _))throw new InvalidOperationException();
                    Sessions.Add(session,new object());ownsSession=true;
                }
                return new(new OwnedSession(session,factory),registration.Descriptor.Id,issues.Count!=0,Array.AsReadOnly(issues.ToArray()));
            }
            catch
            {
                issues.Add(issue);
                if(!ReferenceEquals(registration,_bundled))lock(_failureGate)_failures.TryAdd(registration,issue);
                if(ownsSession&&session is not null)await DisposeFailedAsync(session,MediaTransportIssue.SessionDisposeFailed,issues);
                if(ownsFactory&&factory is not null)await DisposeFailedAsync(factory,MediaTransportIssue.FactoryDisposeFailed,issues);
            }
        }
        throw new MediaTransportComponentException(issues);
    }
    private static async Task DisposeFailedAsync(IAsyncDisposable value,MediaTransportIssue issue,List<MediaTransportIssue> issues)
    { try{await value.DisposeAsync().ConfigureAwait(false);}catch{issues.Add(issue);} }

    private sealed class OwnedSession(IMediaTransportSession session,IMediaTransportFactory factory) : IMediaTransportSession
    {
        private readonly object _gate=new();private Task? _close;
        public async Task<IMediaTransportResource> PrepareAsync(MediaTransportRequest request,string? identity,CancellationToken token,bool prefetch=false)
        {
            lock(_gate)ObjectDisposedException.ThrowIf(_close is not null,this);
            try{return await session.PrepareAsync(request,identity,token,prefetch).ConfigureAwait(false);}
            catch(OperationCanceledException){throw;}
            catch(MediaTransportException){throw;}
            catch{throw new MediaTransportException(MediaTransportFailure.TransportFailure);}
        }
        public ValueTask DisposeAsync()
        {
            lock(_gate)return new(_close??=Task.Run(CloseAsync));
        }
        private async Task CloseAsync()
        {
            var issues=new List<MediaTransportIssue>();
            await DisposeFailedAsync(session,MediaTransportIssue.SessionDisposeFailed,issues).ConfigureAwait(false);
            await DisposeFailedAsync(factory,MediaTransportIssue.FactoryDisposeFailed,issues).ConfigureAwait(false);
            if(issues.Count!=0)throw new MediaTransportComponentException(issues);
        }
    }
}
