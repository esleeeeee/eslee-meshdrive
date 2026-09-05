using MeshDrive.Core;
using MeshDrive.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MeshDrive.Agent;

public static class SyncHttpApi
{
    public static void Map(WebApplication app, SyncFolders folders, SyncInbox inbox, Func<HttpContext, string> peerId, Func<bool> paused)
    {
        string Peer(HttpContext c)
        {
            if (paused()) throw new UnauthorizedAccessException("공유가 일시 중지되었습니다.");
            return peerId(c);
        }
        app.MapGet("/v1/secure/sync/roots", (HttpContext c) =>
        {
            var peer = Peer(c);
            return Results.Json(folders.Snapshot().Where(f => f.AllowedDevices.Contains(peer, StringComparer.Ordinal)).Select(f => new RemoteSyncFolder(f.Id, f.Name)));
        });
        app.MapGet("/v1/secure/sync/inventory", (HttpContext c, string rootId) => Results.Json(folders.Inventory(rootId, Peer(c))));
        app.MapMethods("/v1/secure/sync/content", ["GET", "HEAD"], (HttpContext c, string rootId, string path, string hash) =>
        {
            var local = folders.Resolve(rootId, path, Peer(c));
            if (SyncFolders.FileHash(local) != hash) return Results.StatusCode(412);
            return Results.File(new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true), "application/octet-stream",
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"' + hash + '"'), enableRangeProcessing: true);
        });
        app.MapPost("/v1/secure/sync/upload-start", (HttpContext c, SyncUploadRequest request) => Results.Json(inbox.Begin(Peer(c), request)));
        app.MapPut("/v1/secure/sync/upload-chunk", async (HttpContext c, string id, long offset) =>
        {
            var peer = Peer(c);
            if (c.Request.ContentLength is not long count || count <= 0 || count > SyncInbox.ChunkSize) return Results.BadRequest();
            var bytes = new byte[(int)count]; await c.Request.Body.ReadExactlyAsync(bytes, c.RequestAborted).ConfigureAwait(false);
            inbox.Append(peer, id, offset, bytes); return Results.Ok();
        });
        app.MapPost("/v1/secure/sync/upload-complete", (HttpContext c, string id) => { inbox.Complete(Peer(c), id); return Results.Ok(); });
        app.MapPost("/v1/secure/sync/delete", (HttpContext c, SyncDeleteRequest request) =>
        {
            folders.Apply(request.RootId, request.Path, request.ExpectedHash, null, null, Peer(c)); return Results.Ok();
        });
    }
}
