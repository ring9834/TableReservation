namespace Reservation.Infra;

/// <summary>
/// The CloudFront Function on the static site's viewer requests (Design §17.4). Next's export with
/// <c>trailingSlash: true</c> writes <c>host/index.html</c>; S3 has no directory index behind OAC, so:
/// <c>/host/</c> → <c>/host/index.html</c>, and <c>/host</c> (no file extension) → 301 to <c>/host/</c>, keeping the
/// query string (<c>/manage?token=…</c>). Files (<c>/_next/…js</c>, <c>/host.webmanifest</c>) pass unchanged.
/// </summary>
public static class SiteFunction
{
    public const string Code = """
        function handler(event) {
          var request = event.request;
          var uri = request.uri;
          if (uri.endsWith('/')) {
            request.uri = uri + 'index.html';
            return request;
          }
          var last = uri.substring(uri.lastIndexOf('/') + 1);
          if (last.indexOf('.') !== -1) {
            return request;
          }
          var parts = [];
          var qs = request.querystring;
          for (var key in qs) {
            var entry = qs[key];
            var values = entry.multiValue ? entry.multiValue : [entry];
            for (var i = 0; i < values.length; i++) {
              parts.push(values[i].value === '' ? key : key + '=' + values[i].value);
            }
          }
          var location = uri + '/' + (parts.length > 0 ? '?' + parts.join('&') : '');
          return {
            statusCode: 301,
            statusDescription: 'Moved Permanently',
            headers: { location: { value: location } }
          };
        }
        """;
}
