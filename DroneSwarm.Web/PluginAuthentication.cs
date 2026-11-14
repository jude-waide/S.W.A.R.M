using System.Security.Cryptography;
using System.Text;

namespace DroneSwarm.Web;

internal static class PluginAuthentication
{
    public static bool IsAuthorized(HttpContext context, string? configuredKey)
    {
        var suppliedKey = context.Request.Headers["X-Plugin-Key"].ToString();
        if (string.IsNullOrEmpty(configuredKey) || string.IsNullOrEmpty(suppliedKey))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configuredKey),
            Encoding.UTF8.GetBytes(suppliedKey));
    }

    public static IResult Unauthorized() => Results.Problem(
        statusCode: 401,
        title: "A valid plugin key is required.",
        extensions: new Dictionary<string, object?> { ["code"] = "unauthorized" });
}
