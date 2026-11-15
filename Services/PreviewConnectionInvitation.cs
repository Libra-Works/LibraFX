using System.Text.Json;

namespace LiberaFx.Services;

public static class PreviewConnectionInvitation
{
    public static string Create(string endpoint, string accessToken, string certificateFingerprint)
    {
        var uri = new Uri(endpoint.Replace("<editor-ip>", "127.0.0.1"), UriKind.Absolute);
        if (uri.Scheme != "wss" || uri.AbsolutePath != "/preview" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is < 1 or > 65535 ||
            accessToken.Length != 64 || !accessToken.All(Uri.IsHexDigit) ||
            certificateFingerprint.Length != 64 || !certificateFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid preview connection information.");
        return JsonSerializer.Serialize(new
        {
            type = "librafx_preview_connection", version = 1, host = uri.Host, port = uri.Port,
            accessToken, certificateFingerprint
        });
    }
}
