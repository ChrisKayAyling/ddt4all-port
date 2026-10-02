namespace Ddt4All.App.Services.Real;

/// <summary>
/// Keeps the older <see cref="IEcuContext"/> (Requests / Data Editor pages) and the shared <see cref="IEcuSession"/> in sync:
/// an ECU opened through the session (browser, scan) appears in the context, and an ECU set through the context
/// (editor, file open) becomes the session's current ECU. Reference equality guards against loops.
/// </summary>
public static class EcuSessionContext
{
    public static EcuContext Attach(EcuContext ctx, IEcuSession session)
    {
        ctx.Addresser ??= async ct => await session.EnsureAddressedAsync(ct);
        bool busy = false;
        void FromSession()
        {
            if (busy || session.Ecu is not { } e || ReferenceEquals(ctx.Ecu, e)) return;
            busy = true;
            try { ctx.SetEcu(e, session.Layout, session.SourcePath); } finally { busy = false; }
        }
        session.Changed += FromSession;
        ctx.PropertyChanged += (_, p) =>
        {
            if (busy || p.PropertyName is not (nameof(EcuContext.Ecu)) || ctx.Ecu is not { } e || ReferenceEquals(session.Ecu, e)) return;
            busy = true;
            try { session.SetLoaded(e, ctx.Layout, ctx.SourcePath); } finally { busy = false; }
        };
        FromSession();
        return ctx;
    }
}
