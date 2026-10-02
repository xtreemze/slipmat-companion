using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated preparation and playback of opaque, SSRF-hardened remote-media
/// relay tickets. This is intentionally not an arbitrary URL proxy.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/media-relay")]
public class RemoteMediaRelayController : ControllerBase
{
    private const int MaxRequestBodyBytes = 16 * 1024;

    /// <summary>POST /Plugins/AudioGateway/media-relay/prepare</summary>
    [HttpPost("prepare")]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(RemoteMediaRelayPrepareResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<RemoteMediaRelayPrepareResponse> Prepare(
        [FromBody] RemoteMediaRelayPrepareRequest request)
    {
        try
        {
            return Ok(RemoteMediaRelayService.Prepare(request));
        }
        catch (RemoteMediaRelayService.RelayException ex)
        {
            return Problem(
                statusCode: (int)ex.ResponseStatus,
                title: "Remote media relay preparation failed",
                detail: ex.Code);
        }
    }

    /// <summary>GET /Plugins/AudioGateway/media-relay/{relayId}</summary>
    [HttpGet("{relayId}")]
    public async Task Relay(string relayId)
    {
        Response.Headers["Access-Control-Allow-Origin"] = "*";
        Response.Headers["Access-Control-Expose-Headers"] = "Accept-Ranges, Content-Length, Content-Range";
        Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        Response.Headers["Cache-Control"] = "private, no-store";

        HttpResponseMessage? upstream = null;
        try
        {
            RangeHeaderValue? range = null;
            if (Request.Headers.TryGetValue("Range", out StringValues rangeValue) &&
                !StringValues.IsNullOrEmpty(rangeValue) &&
                !RangeHeaderValue.TryParse(rangeValue.ToString(), out range))
            {
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return;
            }

            upstream = await RemoteMediaRelayService.OpenAsync(
                    relayId,
                    range,
                    now: null,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);

            Response.StatusCode = (int)upstream.StatusCode;
            CopyHeader(upstream.Headers, "Accept-Ranges");
            CopyHeader(upstream.Content.Headers, "Content-Range");
            if (upstream.Content.Headers.ContentLength is long length)
            {
                Response.ContentLength = length;
            }

            if (upstream.Content.Headers.ContentType is MediaTypeHeaderValue contentType)
            {
                Response.ContentType = contentType.ToString();
            }
            else
            {
                Response.ContentType = "application/octet-stream";
            }

            if (upstream.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                await upstream.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        catch (RemoteMediaRelayService.RelayException ex)
        {
            if (!Response.HasStarted)
            {
                Response.StatusCode = (int)ex.ResponseStatus;
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected from a finite or live stream.
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private void CopyHeader(HttpHeaders headers, string headerName)
    {
        if (headers.TryGetValues(headerName, out var values))
        {
            Response.Headers[headerName] = string.Join(", ", values);
        }
    }
}
